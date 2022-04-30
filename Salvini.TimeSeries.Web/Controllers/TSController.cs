using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Salvini.TimeSeries;

[ApiController]
[Route("ts/[action]")]
public class TSController : ControllerBase
{
    private Client _client;
    public TSController(Client client)
    {
        _client = client;
    }

    /// <summary>
    /// 根据测点配置conf/points.csv初始化数据库
    /// </summary> 
    /// <param name="device">所属设备或数据库</param>
    [HttpGet]
    public async Task<object> initialize(string device)
    {
        var path = Path.Combine("wwwroot", "conf", $"points.csv");
        var points = System.IO.File.ReadAllLines(path, Encoding.UTF8).Skip(1).Where(x => !string.IsNullOrEmpty(x)).Select(x => x.Split(',')).Select(x => new JsonObject
        {
            ["tag"] = x[0],
            ["type"] = string.Equals(x[1], "DI", StringComparison.OrdinalIgnoreCase) ? "DI" : "AI",
            ["desc"] = x[2],
            ["unit"] = x[3],
            ["downlimit"] = double.TryParse(x[4], out var down) ? down : default(double?),
            ["uplimit"] = double.TryParse(x[5], out var up) ? up : default(double?),
        }).ToList();
        return await initialize(points, device);
    }

    /// <summary>
    /// 提交测点并初始化数据, 文件路径conf/points.csv,
    /// 点名[tag|_id],类型[type],描述[desc],单位[unit],下限[downlimit],上限[uplimit]
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="measurements">测点数据 {tag:string, type?:'AI'|'DI', desc?:string, unit?:string, downlimit?:number, uplimit?:number}</param> 
    [HttpPost]
    public async Task<object> initialize([FromBody] List<JsonObject> measurements, string device)
    {
        var points = measurements.Select(x => ((string)x["tag"] ?? (string)x["_id"], (string)x["type"], (string)x["desc"], (string)x["unit"], (double?)x["downlimit"] ?? (double?)x["down"], (double?)x["uplimit"] ?? (double?)x["up"])).ToList();
        await _client.InitializeAsync(device, points);
        var result = await _client.PointsAsync(device);
        var path = Path.Combine(Environment.CurrentDirectory, "wwwroot", "conf", "points.csv");
        await result.SaveToCsvAsync(path);
        return new { status = "ok", points = result.ToJsonObject() };
    }

    /// <summary>
    /// 写入实时值, time为null时使用服务器系统时间, args:{device:string,time?:string,data:object[]} => data:{_id:string, value:number}
    /// </summary>
    [HttpPost]
    [ActionName("bulk-write")]
    public async Task<object> bulkWrite([FromBody] object[,] matrix, string device)
    {
        var elapsed = Stopwatch.StartNew();
        var status = "ok";
        var _logger = log4net.LogManager.GetLogger(".NETCoreRepository", "bulk-write");
        try
        {
            await _client.BulkWriteAsync(device, matrix);
            _logger.Info($"invoke BulkWrite, save {matrix.Length / matrix.Rank} items");
        }
        catch (System.Exception ex)
        {
            status = $"数据写入失败,{ex.InnerException?.Message ?? ex.Message}";
            _logger.Fatal($"invoke BulkWrite error: {ex.InnerException?.Message ?? ex.Message}\r\n", ex);
        }
        elapsed.Stop();
        HttpContext.Response.Headers.Add("TimeSeries-BulkWriteAsync", (elapsed.Elapsed.TotalMilliseconds / 1000).ToString("F3"));
        return new { elapsed, status };
    }

    /// <summary>
    /// 获取测点信息, 参数 <see param="keywords" /> 有效正则表达式即可
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="keywords">搜索关键词,对_id字段任何有效查询</param>
    [HttpGet]
    public async Task<object> point(string device, string keywords = "")
    {
        return (await _client.PointsAsync(device, keywords)).ToJsonObject(); ;
    }

    /// <summary>
    ///  读取实时数据,进返回数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tags">测点集合</param> 
    [HttpGet]
    public async Task<object> snapshot(string tags, string device)
    {
        return await snapshot(tags.Split(',', ';').ToList(), device);
    }

    /// <summary>
    ///  读取实时数据,进返回数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tags">测点集合</param> 
    [HttpPost]
    public async Task<object> snapshot([FromBody] List<string> tags, string device)
    {
        var data = (await _client.SnapshotAsync(device, tags));
        return data.Select(x => (x.Tag, x.Value)).ToList().ToJsonObject();
    }

    /// <summary>
    /// 读取归档数据,由接口程序或设备端发送的时序数据形成的归档,数据库真实存在的历史数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点,分号分隔</param>
    /// <param name="start">开始时刻</param>
    /// <param name="end">截止时刻</param>
    /// <param name="digits">数据精度</param>
    /// <param name="utc">是否utc毫秒</param> 
    [HttpGet]
    public async Task<JsonObject> archive(string device, string tag, DateTime start, DateTime end, int digits = 6, bool utc = false)
    {
        var watch = Stopwatch.StartNew();
        var result = new JsonObject();
        foreach (var x in tag.Split(';', ','))
        {
            result[x] = (await _client.ArchiveAsync(device, x, start, end, digits)).ToJsonArray(utc);
        }
        watch.Stop();
        HttpContext.Response.Headers.Add("TimeSeries-ArchiveAsync", (watch.ElapsedMilliseconds / 1000).ToString("F3"));
        return result;
    }

    /// <summary>
    /// 读取历史数据,等间隔的归档数据,自动线性差值的历史数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点,分号分隔</param>
    /// <param name="start">开始时刻</param>
    /// <param name="end">截止时刻</param>
    /// <param name="samples">采样间隔,单位毫秒,默认1秒</param>
    /// <param name="digits">数据精度</param>
    /// <param name="utc">是否utc毫秒</param> 
    [HttpGet]
    public async Task<object> history(string device, string tag, DateTime start, DateTime end, int samples = 1000, int digits = 6, bool utc = false)
    {
        var watch = Stopwatch.StartNew();
        var result = new JsonObject();
        foreach (var x in tag.Split(';', ','))
        {
            result[x] = (await _client.HistoryAsync(device, x, start, end, digits, samples)).ToJsonArray(utc);
        }
        watch.Stop();
        HttpContext.Response.Headers.Add("TimeSeries-HistoryAsync", (watch.ElapsedMilliseconds / 1000).ToString("F3"));
        return result;
    }

    /// <summary>
    /// 获取绘图数据,根据像素间隔,返回特征数据,以表征总体趋势
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点,分号分隔</param>
    /// <param name="start">开始时刻</param>
    /// <param name="end">截止时刻</param>
    /// <param name="px">屏幕像素</param>
    /// <param name="digits">数据小数位</param> 
    /// <param name="utc">是否utc毫秒</param>
    /// <remarks> 通常是查询比较长时间的内趋势,如1周,1个月 </remarks>
    [HttpGet]
    public async Task<object> plot(string device, string tag, DateTime start, DateTime end, int px = 800, int digits = 6, bool utc = false)
    {
        var watch = Stopwatch.StartNew();
        var result = new JsonObject();
        foreach (var x in tag.Split(';', ','))
        {
            result[x] = (await _client.PlotAsync(device, x, start, end, digits, px)).ToJsonArray(utc);
        }
        watch.Stop();
        HttpContext.Response.Headers.Add("TimeSeries-PlotAsync", (watch.ElapsedMilliseconds / 1000).ToString("F3"));
        return result;
    }

    /// <summary>
    /// 导出测点历史Excel数据文件,支持秒级数据,通常是1-2日数据
    /// </summary>
    /// <param name="device">所属设备或数据库</param>
    /// <param name="tag">测点,分号分隔</param>
    /// <param name="start">开始时刻</param>
    /// <param name="end">截止时刻</param>
    /// <param name="digits">数据小数位</param>  
    [HttpGet]
    [ActionName("export-xlsx")]
    public async Task<FileResult> exportXlsx(string device, string tag, DateTime start, DateTime end, int digits = 6)
    {
        var fileName = $"历史数据[{start:yy年MM月dd日HH时mm分ss秒}-{end:yy年MM月dd日HH时mm分ss秒}].csv";
        var watch = Stopwatch.StartNew();
        var keywords = string.Join("|", tag.Split(';', ',').Select(x => $"^{x}$"));
        var points = await _client.PointsAsync(device, keywords);
        var sheets = new List<(string Name, List<(DateTime Time, double Value)> Data)>();
        var tags = tag.Split(';', ',').ToList();
        var result = await _client.HistoryxAsync(device, tags, start, end, digits, 1000);
        var path = Path.Combine(Environment.CurrentDirectory, "wwwroot", "downloads", "csv", fileName);
        await result.SaveToCsvAsync(path);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var stream = System.IO.File.OpenRead(path);
        watch.Stop();
        Response.Headers.Add("TimeSeries-ExcelAsync", (watch.ElapsedMilliseconds / 1000).ToString("F3"));
        ThreadPool.QueueUserWorkItem(async (_) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            System.IO.File.Delete(path);
        });
        return new FileStreamResult(stream, "plain/text") { FileDownloadName = $"{fileName}" };
    }

    /// <summary>
    /// 导出数据库测点文件csv
    /// </summary> 
    /// <param name="device">所属设备或数据库</param>
    [HttpGet]
    [ActionName("export-measurements")]
    public async Task<FileResult> exportMeasurements(string device)
    {
        var result = await _client.PointsAsync(device);
        var fileName = $"[{device}]数据库点表文件.csv";
        var path = Path.Combine(Environment.CurrentDirectory, "wwwroot", "downloads", "csv", fileName);
        await result.SaveToCsvAsync(path);
        var stream = System.IO.File.OpenRead(path);
        ThreadPool.QueueUserWorkItem((_) =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(5));
            System.IO.File.Delete(path);
        });
        return new FileStreamResult(stream, "text/csv") { FileDownloadName = fileName };
    }
}
