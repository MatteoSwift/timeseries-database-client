using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Salvini.IoTDB;
using Salvini.IoTDB.Data;

namespace Salvini.TimeSeries;

public class IoTDBClient : Salvini.TimeSeriesClient
{
    private readonly Session session;

    public IoTDBClient(string url) : base(url)
    {
        //url = iotdb://root:admin#123@127.0.0.1:6667/?appName=iTSDB&fetchSize=1800
        var match_host = new Regex(@"@((2(5[0-5]|[0-4]\d))|[0-1]?\d{1,2})(\.((2(5[0-5]|[0-4]\d))|[0-1]?\d{1,2})){3}:").Match(url);
        var match_port = new Regex(@":(\d{1,5})/?").Match(url);
        var match_user = new Regex(@"iotdb://(\w+):").Match(url);
        var match_pwd = new Regex(@":(\w+\S+){1}(@)").Match(url);

        var host = match_host.Success ? match_host.Value[1..^1] : "127.0.0.1";
        var port = match_port.Success ? int.Parse(match_port.Value[1..].Replace("/", string.Empty)) : 6667;
        var username = match_user.Success ? match_user.Value[8..^1] : "root";
        var password = match_pwd.Success ? match_pwd.Value[1..^1] : "root";
        var fetchSize = 1800;

        session = new Session(host, port, username, password, fetchSize);
        session.OpenAsync().Wait(TimeSpan.FromSeconds(5));
    }

    public override async Task<List<(string Tag, DateTime Time, double Value)>> SnapshotAsync(string device, List<string> tags)
    {
        var len = device.Length + 6;
        var sql = $"select last {string.Join(",", tags)} from root.{device}";
        using var query = await session.ExecuteQueryStatementAsync(sql);
        var data = new List<(string Tag, DateTime Time, double Value)>();
        while (query.HasNext())
        {
            var next = query.Next();
            var values = next.Values;
            var id = ((string)values[0])[len..];
            var time = next.GetDateTime();
            var value = values[1] == null ? double.NaN : double.Parse((string)values[1]);
            data.Add((id, time, value));
        }
        return data;
    }

    public override async Task<List<(DateTime Time, double Value)>> ArchiveAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6)
    {
        var sql = $"select {tag} from root.{device} where time>={begin:yyyy-MM-dd HH:mm:ss.fff} and time<={end:yyyy-MM-dd HH:mm:ss.fff} align by device";
        using var query = await session.ExecuteQueryStatementAsync(sql);
        var data = new List<(DateTime Time, double Value)>();
        while (query.HasNext())
        {
            var next = query.Next();
            var values = next.Values;
            var time = next.GetDateTime();
            var value = "NULL".Equals(values[1]) ? double.NaN : (double)values[1];
            data.Add((time, value));
        }
        return data;
    }

    public override async Task<List<(DateTime Time, double Value)>> HistoryAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        var @break = false;
        var data = new List<(DateTime Time, double Value)>();
        if ((end - begin).TotalHours > 4)//4小时以内可以不检测数据是否存在
        {
            var sql = $"select count({tag}) as exist from root.{device} where time >= {begin:yyyy-MM-dd HH:mm:ss} and time < {end:yyyy-MM-dd HH:mm:ss}";
            using var query = await session.ExecuteQueryStatementAsync(sql);
            @break = query.HasNext() && (long)query.Next().Values[0] == 0;
        }
        if (!@break)
        {
            var sql = $"select last_value({tag}) as {tag} from root.{device} group by ([{begin:yyyy-MM-dd HH:mm:ss},{end.AddMilliseconds(ms):yyyy-MM-dd HH:mm:ss}), {ms}ms) fill(double[previous])";
            using var query = await session.ExecuteQueryStatementAsync(sql);
            while (query.HasNext())
            {
                var next = query.Next();
                var values = next.Values;
                var time = next.GetDateTime();
                var value = "NULL".Equals(values[0]) ? double.NaN : (double)values[0];
                data.Add((time, value));
            }
        }
        return data;

    }

    public override async Task BulkWriteAsync(string device, dynamic[,] matrix)
    {
        var rows = matrix.GetUpperBound(0) + 1;
        var columns = matrix.GetUpperBound(1) + 1;
        if (rows != 0)
        {
            var cols = Enumerable.Range(1, columns - 1).ToList();
            var measurements = cols.Select((j) => ((string)matrix[0, j]).Replace("root.", string.Empty)).ToList();
            if (rows == 2)
            {
                var values = cols.Select((j) => matrix[1, j]).ToList();
                var record = new RowRecord(UTC_MS(matrix[1, 0]), values, measurements);
                var effect = await session.InsertRecordAsync($"root.{device}", record, false);
            }
            else
            {
                var timestamps = new List<DateTime>();
                var values = new List<List<dynamic>>();
                for (var i = 1; i < rows; i++)
                {
                    timestamps.Add(matrix[i, 0]);
                    values.Add(cols.Select(j => matrix[i, j]).ToList());
                }
                var tablet = new Tablet($"root.{device}", measurements, values, timestamps);
                var effect = await session.InsertTabletAsync(tablet, false);
            }
        }
    }

    public override async Task InitializeAsync(string device, List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit)> measurements)
    {
        var exist = await PointsAsync(device);
        foreach (var point in measurements)
        {
            var _id = point.Tag;
            string sql;
            if (exist.Any(x => x.Tag == _id))
            {
                sql = $"alter timeseries root.{device}.{_id} upsert tags (t='{point.Type}', u='{point.Unit}', d='{point.Desc}', @t='{DateTime.Now:yyyy-MM-dd HH:mm:ss}')";
            }
            else
            {
                sql = $"create timeseries root.{device}.{_id} with datatype=DOUBLE tags ( t='{point.Type}', u='{point.Unit}', d='{point.Desc}', @t='{DateTime.Now:yyyy-MM-dd HH:mm:ss}')";
            }
            if (point.Downlimit != null && point.Uplimit != null)
            {
                sql += $" attributes (l='{point.Downlimit}',h='{point.Uplimit}')";
            }
            var effect = await session.ExecuteNonQueryStatementAsync(sql);
            Console.WriteLine($"iotdb:InitializeAsync:{effect}>>{sql}");
        }
    }

    public override async Task<bool> OpenAsync()
    {
        await session.OpenAsync();
        return true;
    }

    public override async Task<bool> CloseAsync()
    {
        await session.CloseAsync();
        return true;
    }

    public override async Task<bool> DropAsync(string device)
    {
        var effect = await session.DeleteStorageGroupAsync("root." + device);
        return true;
    }

    public override async Task<List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? modifyTime)>> PointsAsync(string device, string keywords = "")
    {
        if (keywords?.StartsWith("/") == true) keywords = keywords[1..];
        if (keywords?.EndsWith("/") == true) keywords = keywords[0..^1];
        if (keywords?.EndsWith("/i") == true) keywords = keywords[0..^2];
        static JsonObject DeserializeObject(string json)
        {
            if (!string.IsNullOrEmpty(json) && json != "NULL")
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonObject>(json.Replace("\\", "/"));
                }
                catch (System.Exception ex)
                {

                }
            }
            return new JsonObject();
        }
        using var query = await session.ExecuteQueryStatementAsync($"show timeseries root.{device}");
        var points = new List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? @modifyTime)>();
        var len = device.Length + 6;
        var reg = new Regex(keywords ?? "", RegexOptions.IgnoreCase);
        while (query.HasNext())
        {
            var next = query.Next();
            var values = next.Values;
            var id = ((string)values[0])[len..];
            if (!reg.IsMatch(id)) continue;
            var tags = DeserializeObject((string)values[^2]);
            var attributes = DeserializeObject((string)values[^1]);
            points.Add((id, (string)tags?["t"], (string)tags?["d"], (string)tags?["u"], double.TryParse((string)attributes?["l"], out var l) ? l : default(double?), double.TryParse((string)attributes?["h"], out var h) ? h : default(double?), DateTime.TryParse((string)tags?["@t"], out var modify) ? modify : default(DateTime?)));
        }
        return points.OrderBy(x => x.Tag).ToList();
    }
}