using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StockTickerLite.Models;

namespace StockTickerLite.Services;

public sealed partial class SinaQuoteService : IDisposable
{
    private readonly HttpClient _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal> _circulatingShares = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _industries = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> _concepts = new(StringComparer.OrdinalIgnoreCase);

    public SinaQuoteService()
    {
        _client = DirectHttpClient.Create(
            TimeSpan.FromSeconds(8),
            "https://hq.sinajs.cn/");
        _client.DefaultRequestHeaders.Referrer = new Uri("https://finance.sina.com.cn/");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StockTickerLite/1.0");
    }

    public async Task<IReadOnlyDictionary<string, StockQuote>> GetQuotesAsync(
        IEnumerable<string> codes,
        CancellationToken cancellationToken,
        bool includeExtendedInfo = false)
    {
        var normalizedCodes = codes
            .Select(StockCode.Normalize)
            .Where(StockCode.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedCodes.Length == 0)
            return new Dictionary<string, StockQuote>();

        var missingExtendedInfoCodes = includeExtendedInfo
            ? normalizedCodes.Where(code => !IsMarketIndex(code) &&
                (!_circulatingShares.ContainsKey(code) || !_industries.ContainsKey(code) || !_concepts.ContainsKey(code))).ToArray()
            : [];
        var requestCodes = normalizedCodes.Concat(missingExtendedInfoCodes.Select(code => code + "_i"));
        var path = "list=" + string.Join(',', requestCodes);
        var bytes = await _client.GetByteArrayAsync(path, cancellationToken);
        var response = Encoding.GetEncoding("GB18030").GetString(bytes);

        var result = new Dictionary<string, StockQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in QuoteLineRegex().Matches(response))
        {
            var code = match.Groups["code"].Value.ToLowerInvariant();
            var fields = match.Groups["data"].Value.Split(',');
            var quote = Parse(code, fields);
            if (quote is not null)
                result[code] = quote;
        }

        if (includeExtendedInfo)
        {
            foreach (Match match in ExtendedInfoLineRegex().Matches(response))
            {
                var code = match.Groups["code"].Value.ToLowerInvariant();
                if (!result.TryGetValue(code, out var quote))
                    continue;

                var fields = match.Groups["data"].Value.Split(',');
                var circulatingSharesInTenThousands = Decimal(fields, 8);
                if (circulatingSharesInTenThousands > 0)
                    _circulatingShares[code] = circulatingSharesInTenThousands * 10000;
                _industries[code] = fields.Length > 34 ? fields[34].Trim() : "";
                _concepts[code] = fields.Length > 40
                    ? fields[40].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : [];
            }
        }

        foreach (var code in result.Keys.ToArray())
        {
            var quote = result[code];
            if (_circulatingShares.TryGetValue(code, out var circulatingShares))
                quote = quote with { CirculatingShares = circulatingShares };
            if (_industries.TryGetValue(code, out var industry))
                quote = quote with { Industry = industry };
            if (_concepts.TryGetValue(code, out var concepts))
                quote = quote with { Concepts = concepts };
            result[code] = quote;
        }

        return result;
    }

    private static bool IsMarketIndex(string code) =>
        code.StartsWith("sh000", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("sz399", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("bj899", StringComparison.OrdinalIgnoreCase);

    private static StockQuote? Parse(string code, string[] fields)
    {
        // 【新增】港股走独立解析逻辑
        if (code.StartsWith("hk"))
            return ParseHk(code, fields);

        // ===== 原有 A 股逻辑保持不变 =====
        if (fields.Length < 10 || string.IsNullOrWhiteSpace(fields[0]))
            return null;

        var current = Decimal(fields, 3);
        var previousClose = Decimal(fields, 2);
        if (current == 0 && previousClose == 0)
            return null;

        var isPreMarketFallback = current <= 0 && previousClose > 0;
        if (isPreMarketFallback)
            current = previousClose;

        var open = Decimal(fields, 1);
        var high = Decimal(fields, 4);
        var low = Decimal(fields, 5);
        if (isPreMarketFallback)
            open = high = low = previousClose;

        DateTime? quoteTime = null;
        if (fields.Length > 31 &&
            DateTime.TryParseExact(
                $"{fields[30]} {fields[31]}",
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            quoteTime = parsedTime;
        }

        return new StockQuote(
            code,
            fields[0].Trim(),
            current,
            previousClose,
            open,
            high,
            low,
            Long(fields, 8),
            Decimal(fields, 9),
            Long(fields, 10),
            Decimal(fields, 11),
            Long(fields, 20),
            Decimal(fields, 21),
            quoteTime,
            isPreMarketFallback);
    }

    // 【新增】港股字段解析（经 curl 实测确认字段映射）
    private static StockQuote? ParseHk(string code, string[] fields)
    {
        // 实测报文字段映射（curl "http://hq.sinajs.cn/list=hk00700,hk00005" 验证）：
        // fields[0]  = 英文名
        // fields[1]  = 中文名
        // fields[2]  = 今开
        // fields[3]  = 昨收
        // fields[4]  = 最高
        // fields[5]  = 最低
        // fields[6]  = 现价
        // fields[7]  = 涨跌额
        // fields[8]  = 涨跌幅(%)
        // fields[9]  = 买一价
        // fields[10] = 卖一价
        // fields[11] = 成交金额(HKD)
        // fields[12] = 成交量(股)
        // fields[13] = (港股无此字段，返回0.000)
        // fields[14] = (港股无此字段，返回0.000)
        // fields[15] = (未知，暂不映射)
        // fields[16] = (未知，暂不映射)
        // fields[17] = 日期 (yyyy/MM/dd)
        // fields[18] = 时间 (HH:mm)

        if (fields.Length < 7 || string.IsNullOrWhiteSpace(fields[1]))
            return null;

        var name = fields[1].Trim();                  // 中文名
        var prevClose = Decimal(fields, 3);           // 昨收
        var current = Decimal(fields, 6);             // 现价
        var open = Decimal(fields, 2);                // 今开
        var high = Decimal(fields, 4);                // 最高
        var low = Decimal(fields, 5);                 // 最低

        if (current == 0 && prevClose == 0)
            return null;

        var isPreMarketFallback = current <= 0 && prevClose > 0;
        if (isPreMarketFallback)
            current = prevClose;

        // 成交量/金额 —— 之前版本索引写错了，现已修正
        var volume = Long(fields, 12);                // 成交量(股)
        var amount = Decimal(fields, 11);             // 成交金额(HKD)

        // 买一价/卖一价（港股无盘口量数据）
        var bid1Price = Decimal(fields, 9);           // 买一价
        var ask1Price = Decimal(fields, 10);          // 卖一价
        var bid1Volume = 0L;                          // 港股无此数据
        var ask1Volume = 0L;                          // 港股无此数据

        // 解析时间 (港股日期格式: yyyy/MM/dd，与A股 yyyy-MM-dd 不同)
        DateTime? quoteTime = null;
        if (fields.Length > 18 &&
            DateTime.TryParseExact(
                $"{fields[17]} {fields[18]}",
                "yyyy/MM/dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            quoteTime = parsedTime;
        }

        return new StockQuote(
            code,
            name,
            current,
            prevClose,
            open,
            high,
            low,
            volume,
            amount,
            bid1Volume,
            bid1Price,
            ask1Volume,
            ask1Price,
            quoteTime,
            isPreMarketFallback);
    }

    private static decimal Decimal(string[] fields, int index) =>
        index < fields.Length &&
        decimal.TryParse(fields[index], NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static long Long(string[] fields, int index) =>
        index < fields.Length &&
        long.TryParse(fields[index], NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    public void Dispose() => _client.Dispose();

    // 【改动】\d{6} → \d{5,6}，兼容港股 5 位代码
    [GeneratedRegex(
        @"var\s+hq_str_(?<code>[a-z]{2}\d{5,6})=""(?<data>[^""]*)"";",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex QuoteLineRegex();

    // 【改动】\d{6} → \d{5,6}，兼容港股 5 位代码
    [GeneratedRegex(
        @"var\s+hq_str_(?<code>[a-z]{2}\d{5,6})_i=""(?<data>[^""]*)"";",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedInfoLineRegex();
}
