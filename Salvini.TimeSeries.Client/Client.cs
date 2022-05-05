
namespace Salvini.TimeSeries;

/// <summary>
/// 数据库客户端驱动接口
/// </summary>
public abstract partial class Client
{
    public static Client Create(string url)
    {
        if (url.StartsWith("mongodb://")) return new MongoDBClient(url);
        if (url.StartsWith("iotdb://")) return new IoTDBClient(url);
        throw new Exception($"not support connection url=>{url}");
    }
    /// <summary>
    /// 测点不存在描述
    /// </summary>
    protected const string PT_NOT_EXIST = "does not exist";
    private readonly static DateTime __UTC_TICKS__ = new DateTime(1970, 01, 01).Add(TimeZoneInfo.Local.BaseUtcOffset);
    protected long UTC_MS(DateTime time) => (long)(time - __UTC_TICKS__).TotalMilliseconds;

    public string Url { get; }

    protected Client(string url)
    {
        this.Url = url;
    }

    /// <summary>
    /// 连接数据库
    /// </summary>
    public abstract Task<bool> OpenAsync();

    /// <summary>
    /// 关闭数据库
    /// </summary>
    public abstract Task<bool> CloseAsync();

    /// <summary>
    /// 删除设备或数据库
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    public abstract Task<bool> DropAsync(string device);

    /// <summary>
    /// 初始化数据测点
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="measurements">测点信息</param>
    public abstract Task InitializeAsync(string device, List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit)> measurements);

    /// <summary>
    /// 批量写入数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="matrix">测点和数据矩阵</param>
    public abstract Task BulkWriteAsync(string device, dynamic[,] matrix);

    /// <summary>
    /// 单测点历史数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点</param>
    /// <param name="data">数据集合</param>
    public async Task BulkWriteAsync(string device, string tag, List<(DateTime Time, double Value)> data)
    {
        var matrix = new dynamic[data.Count + 1, 2];
        matrix[0, 0] = "Timestamp";
        matrix[0, 1] = tag;
        for (int i = 0; i < data.Count; i++)
        {
            matrix[i + 1, 0] = data[i].Time;
            matrix[i + 1, 1] = data[i].Value;
        }
        await BulkWriteAsync(device, matrix);
    }


    /// <summary>
    /// 多测点单时刻数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="time">时间戳</param> 
    /// <param name="data">测点数据</param>
    public virtual async Task BulkWriteAsync(string device, DateTime time, List<(string Tag, double Value)> data)
    {
        var matrix = new dynamic[2, data.Count + 1];
        matrix[0, 0] = "Timestamp";
        matrix[1, 0] = time;
        for (int j = 0; j < data.Count; j++)
        {
            matrix[0, j + 1] = data[j].Tag;
            matrix[1, j + 1] = data[j].Value;
        }
        await BulkWriteAsync(device, matrix);
    }

    /// <summary>
    /// 搜索测点
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="keywords">关键字</param> 
    public abstract Task<List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? @modifyTime)>> PointsAsync(string device, string keywords = "");

    /// <summary>
    /// 获取测点快照数据
    /// </summary>
    /// <param name="Tag">测点</param>
    /// <param name="Time">时间</param>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tags">测点集合</param>
    public abstract Task<List<(string Tag, DateTime Time, double Value)>> SnapshotAsync(string device, List<string> tags);

    /// <summary>
    /// 获取测点归档数据,返回<time,value>
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    public abstract Task<List<(DateTime Time, double Value)>> ArchiveAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6);

    /// <summary>
    /// 获取测点归档数据,返回<time,value>
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tags">测点集合</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    public virtual async Task<List<(string Tag, List<(DateTime Time, double Value)> Data)>> ArchiveAsync(string device, List<string> tags, DateTime begin, DateTime end, int digits = 6)
    {
        var lst = new List<(string Tag, List<(DateTime Time, double Value)> Data)>();
        foreach (var tag in tags)
        {
            lst.Add((tag, await ArchiveAsync(device, tag, begin, end, digits)));
        }
        return lst;
    }

    /// <summary>
    /// 获取测点历史数据,等间隔采样,返回<time,value>
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    /// <param name="ms">采样间隔,单位毫秒,默认1秒</param> 
    public virtual async Task<List<(DateTime Time, double Value)>> HistoryAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        return (await ArchiveAsync(device, tag, begin, end, digits)).Fill(begin, end, ms);
    }

    /// <summary>
    /// 获取测点历史数据,等间隔采样,返回<time,value>
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tags">测点</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    /// <param name="ms">采样间隔,单位毫秒,默认1秒</param> 
    public virtual async Task<List<(string Tag, List<(DateTime Time, double Value)> Data)>> HistoryAsync(string device, List<string> tags, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        var lst = new List<(string Tag, List<(DateTime Time, double Value)> Data)>();
        foreach (var tag in tags)
        {
            lst.Add((tag, await HistoryAsync(device, tag, begin, end, digits)));
        }
        return lst;
    }

    public virtual async Task<(DateTime Start, DateTime End, string Tag, double[] Values)> HistoryxAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        var hist = (await HistoryAsync(device, tag, begin, end, digits));
        return (begin, end, tag, hist.Select(x => x.Value).ToArray());
    }
    public virtual async Task<(DateTime Start, DateTime End, string[] Tags, double[,] Matrix)> HistoryxAsync(string device, List<string> tags, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        var hist = new List<double[]>();
        foreach (var tag in tags)
        {
            hist.Add((await HistoryxAsync(device, tag, begin, end, digits, ms)).Values);
        }
        var size = (long)(end - begin).TotalMilliseconds / ms;
        var matrix = new double[size, tags.Count];

        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < matrix.Rank; j++)
            {
                matrix[i, j] = hist[j][i];
            }
        }
        return (begin, end, tags.Select(x => x).ToArray(), matrix);
    }

    /// <summary>
    /// 获取测点绘图数据,返回<time,value>
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    /// <param name="px">屏幕像素,默认1200</param>
    public virtual async Task<List<(DateTime Time, double Value)>> PlotAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6, int px = 1200)
    {
        var raw = await ArchiveAsync(device, tag, begin, end, digits);
        var ts = end - begin;
        if (raw.Count > px && ts.Hours > 6)
        {
            return ByPx();
        }
        else
        {
            return raw;
        }

        List<(DateTime Time, double Value)> ByPx()
        {
            var plot = new List<(DateTime Time, double Value)>();
            var span = Math.Floor(ts.TotalSeconds / px);
            Enumerable.Range(0, px).AsParallel().ForAll(i => plot.Add(raw.LastOrDefault(x => x.Time >= begin.AddSeconds(span))));
            plot = plot.OrderBy(x => x.Time).Where(x => x.Time > Extensions.UTC).ToList();
            if (plot.Any() && plot[plot.Count - 1].Time != end) plot.Add(raw[raw.Count - 1]);
            return plot;
        }
    }


    /// <summary>
    /// 获取测点绘图数据,返回<time,value>[]
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点集合</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="digits">数据精度,默认6位小数</param> 
    /// <param name="px">屏幕像素,默认1200</param>
    public virtual async Task<List<(string Tag, List<(DateTime Time, double Value)>)>> PlotAsync(string device, string[] tags, DateTime begin, DateTime end, int digits = 6, int px = 1200)
    {
        var plot = new List<(string Tag, List<(DateTime Time, double Value)>)>();
        foreach (var tag in tags) plot.Add((tag, await PlotAsync(device, tag, begin, end, digits, px)));
        return plot;
    }
}