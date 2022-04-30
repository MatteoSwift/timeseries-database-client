using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Salvini.TimeSeries;

/// <summary>
/// NoSQL辅助函数
/// </summary>
public static partial class Extensions
{
    internal static DateTime UTC = new DateTime(1970, 01, 01).Add(TimeZoneInfo.Local.BaseUtcOffset);

    public static JsonObject ToJsonObject(this List<(string Tag, List<(DateTime Time, double Value)> Data)> source)
    {
        var jObject = new JsonObject();
        foreach (var kv in source)
        {
            jObject[kv.Tag] = new JsonArray(kv.Data.Select(x => new JsonObject { ["time"] = x.Time, ["value"] = x.Value }).ToArray());
        }
        return jObject;
    }

    public static JsonObject ToJsonObject(this List<(string _id, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? @modifyTime)> source)
    {
        var jObject = new JsonObject();
        source?.ForEach(row =>
        {
            jObject[row._id] = new JsonObject
            {
                ["tag"] = row._id,
                ["type"] = row.Type,
                ["unit"] = row.Unit,
                ["downlimit"] = row.Downlimit,
                ["uplimit"] = row.Uplimit,
                ["desc"] = row.Desc,
                ["@modifyTime"] = row.modifyTime,
            };
        });
        return jObject;
    }

    public static JsonObject ToJsonObject(this List<(string _id, double Value)> data)
    {
        var jObject = new JsonObject();
        data?.ForEach(row =>
        {
            jObject[row._id] = row.Value;
        });
        return jObject;
    }

    public static JsonArray ToJsonArray(this List<(string _id, double Value)> data)
    {
        var jArray = new JsonArray();
        data?.ForEach(row =>
        {
            jArray.Add(new JsonObject
            {
                ["_id"] = row._id,
                ["value"] = row.Value,
            });
        });
        return jArray;
    }

    public static JsonArray ToJsonArray(this List<(DateTime Time, double Value)> data, bool utc = false)
    {
        var jArray = new JsonArray();
        data?.ForEach(row =>
        {
            if (utc)
            {// chart图形数据
                jArray.Add(new JsonObject
                {
                    ["x"] = (long)(row.Time - UTC).TotalMilliseconds,
                    ["y"] = row.Value,
                });
            }
            else
            {
                jArray.Add(new JsonObject
                {
                    ["time"] = row.Time,
                    ["value"] = row.Value,
                });
            }
        });
        return jArray;
    }


    /// <summary>
    ///    原始值补齐，以确保在指定时刻有数据记录，根据阶梯/方波方式计算
    /// </summary>
    /// <param name="source">原数据集合</param>
    /// <param name="begin">开始时间</param>
    /// <param name="end">结束时间</param>
    /// <param name="interval">数据采样间隔,毫秒</param>
    public static List<(DateTime Time, double Value)> Fill(this List<(DateTime Time, double Value)> source, DateTime begin, DateTime end, double interval = 1000)
    {
        var rows = new List<(DateTime Time, double Value)>();
        if (source.All(x => double.IsNaN(x.Value))) return rows;
        if (source.Count > 1)
        {

            var s0 = source[0];
            var t = begin;

            while (t < s0.Time)
            {
                rows.Add((t, s0.Value)); //向前拉直线
                t = t.AddMilliseconds(interval);
            }
            rows.Add((t, s0.Value));
            t = t.AddMilliseconds(interval);
            for (var i = 1; i < source.Count; i++)
            {
                while (t < source[i].Time)
                {
                    rows.Add((t, source[i - 1].Value));
                    t = t.AddMilliseconds(interval);
                }
            }
            while (t <= end)
            {
                rows.Add((t, source[^1].Value));
                t = t.AddMilliseconds(interval);
            }
        }
        else if (source.Count == 1)
        {
            var s0 = source[0];
            var t = begin;
            while (t < s0.Time)
            {
                rows.Add((t, s0.Value));
                t = t.AddMilliseconds(interval);
            }
            rows.Add((t, s0.Value));
            t = t.AddMilliseconds(interval);
            while (t <= end)
            {
                rows.Add((t, s0.Value));
                t = t.AddMilliseconds(interval);
            }
        }
        return rows;
    }
}

public static partial class Extensions
{
    public static async Task SaveToCsvAsync(this (DateTime Start, DateTime End, string[] Tags, double[,] Matrix) data, string fileName)
    {
        var directory = Path.GetDirectoryName(fileName);
        if (!Directory.Exists(directory) && !string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fileName, $"Timestamp,{string.Join(",", data.Tags)}", Encoding.UTF8);
        var size = data.Matrix.Length / data.Matrix.Rank;
        var time = data.Start;
        var delta = (int)((data.End - data.Start).TotalMilliseconds / size);
        for (var i = 0; i < size; i++)
        {
            var line = data.Tags.Select((_, j) => data.Matrix[i, j]).ToArray();
            await File.WriteAllTextAsync(fileName, $"\r\n{time.AddMilliseconds(delta * i):'yyyy-MM-dd HH:mm:ss'},{string.Join(",", line)}", Encoding.UTF8);
        }
    }

    /// <summary>
    /// 点名[tag|_id],类型[type],描述[desc],单位[unit],下限[downlimit],上限[uplimit],维护时间[@modifyTime]
    /// </summary>
    /// <param name="measurements">测点列表</param>
    /// <param name="fileName">文件名</param>
    public static async Task SaveToCsvAsync(this List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? @modifyTime)> measurements, string fileName)
    {
        var directory = Path.GetDirectoryName(fileName);
        if (!Directory.Exists(directory) && !string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fileName, "点名[tag|_id],类型[type],描述[desc],单位[unit],下限[downlimit],上限[uplimit],维护时间[@modifyTime]", Encoding.UTF8);
        foreach (var row in measurements)
        {
            await File.AppendAllTextAsync(fileName, $"\r\n{row.Tag},{row.Type},{row.Desc?.Replace(',', ';')},{row.Unit},{row.Downlimit},{row.Uplimit},{row.modifyTime:yyyy-MM-dd HH:mm:ss}", Encoding.UTF8);
        }
    }

    /// <summary>
    /// 点名[tag|_id],类型[type],描述[desc],单位[unit],下限[downlimit],上限[uplimit]
    /// </summary>
    /// <param name="measurements">测点列表</param>
    /// <param name="fileName">文件名</param>
    public static async Task SaveToCsvAsync(this List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit)> measurements, string fileName)
    {
        var directory = Path.GetDirectoryName(fileName);
        if (!Directory.Exists(directory) && !string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fileName, "点名[tag|_id],类型[type],描述[desc],单位[unit],下限[downlimit],上限[uplimit]", Encoding.UTF8);
        foreach (var row in measurements)
        {
            await File.AppendAllTextAsync(fileName, $"\r\n{row.Tag},{row.Type},{row.Desc?.Replace(',', ';')},{row.Unit},{row.Downlimit},{row.Uplimit}", Encoding.UTF8);
        }
    }

}
