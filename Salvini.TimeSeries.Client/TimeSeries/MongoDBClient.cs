using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Salvini.TimeSeries;

public class MongoDBClient : Salvini.TimeSeriesClient
{
    private const string CN_POINT = "point";
    private const string CN_SNAPSHOT = "snapshot";
    private const string CN_ARCHIVE = "archive";
    private const string TS_DATE_FMT = "yyyyMMdd";
    private readonly MongoClient client;

    public MongoDBClient(string url) : base(url)
    {
        //url=mongodb://root:admin#123@127.0.0.1:27017/admin?appName=iTSDB&connectTimeoutMS=1200&serverSelectionTimeoutMS=1500
        client = new MongoClient(url);
    }

    public override async Task<bool> OpenAsync()
    {
        return true;
    }

    public override async Task<bool> CloseAsync()
    {
        return true;
    }

    public override async Task<bool> DropAsync(string device)
    {
        await client.DropDatabaseAsync(device);
        return true;
    }

    public override async Task BulkWriteAsync(string device, dynamic[,] matrix)
    {
        var rank = matrix.Rank;
        var size = matrix.Length / rank;

        if (size != 0)
        {
            var cols = Enumerable.Range(1, rank).ToList();
            var measurements = cols.Select((j) => ((string)matrix[0, j]).Replace("root.", string.Empty)).ToList();
            if (size == 2)
            {
                var time = (DateTime)matrix[1, 0];
                var hh = $"{time:HH}";
                var mm = $"{time:mm}";
                var ss = $"{time:ss}";
                for (var j = 0; j < measurements.Count; j++)
                {
                    var _id = measurements[j];
                    var key = $"{_id}#{time.ToString(TS_DATE_FMT)}";
                    var findarchive = new FilterDefinitionBuilder<BsonDocument>().Eq("_id", key);
                    var bson = (await client.GetDatabase(device).GetCollection<BsonDocument>(CN_ARCHIVE).FindAsync(findarchive)).FirstOrDefault();
                    if (bson == null)
                    {
                        bson = new BsonDocument { ["_id"] = key, ["date"] = $"{time:yyyy-MM-dd}" };
                    }
                    if (!bson.Contains(hh)) bson[hh] = new BsonDocument();
                    if (!((BsonDocument)bson[hh]).Contains(mm)) bson[hh][mm] = new BsonDocument();
                    if (!((BsonDocument)bson[hh][mm]).Contains(ss)) { ((BsonDocument)bson[hh][mm]).Add(ss, matrix[1, j + 1]); }
                    else { bson[hh][mm][ss] = matrix[1, j + 1]; }
                    await client.GetDatabase(device).GetCollection<BsonDocument>(CN_ARCHIVE).ReplaceOneAsync(findarchive, bson, new ReplaceOptions { IsUpsert = true });

                    var ts = $"{time:yyyy-MM-dd HH:mm:ss}";
                    var value = matrix[1, j + 1];
                    var upsnapshot = new UpdateDefinitionBuilder<BsonDocument>().Set("_id", _id).Set("time", ts).Set("value", (object)value);
                    var findsnapshot = new FilterDefinitionBuilder<BsonDocument>().Eq("_id", _id);
                    await client.GetDatabase(device).GetCollection<BsonDocument>(CN_SNAPSHOT).UpdateOneAsync(findsnapshot, upsnapshot, new UpdateOptions { IsUpsert = true });
                }
            }
            else
            {
                async Task<(BsonDocument, FilterDefinition<BsonDocument>)> ReadOne(string _id, DateTime day)
                {
                    var key = $"{_id}#{day.ToString(TS_DATE_FMT)}";
                    var findarchive = new FilterDefinitionBuilder<BsonDocument>().Eq("_id", key);
                    var bson = (await client.GetDatabase(device).GetCollection<BsonDocument>(CN_ARCHIVE).FindAsync(findarchive)).FirstOrDefault();
                    if (bson == null) bson = new BsonDocument { ["_id"] = key, ["date"] = $"{day:yyyy-MM-dd}" };
                    return (bson, findarchive);
                }

                for (var j = 0; j < measurements.Count; j++)
                {
                    var _id = measurements[j];
                    var time = (DateTime)matrix[1, 0];
                    (var bson, var findarchive) = await ReadOne(_id, time);
                    for (int i = 1; i < size; i++)
                    {
                        if (((DateTime)matrix[i, 0]).Date != time.Date)
                        {
                            await client.GetDatabase(device).GetCollection<BsonDocument>(CN_ARCHIVE).ReplaceOneAsync(findarchive, bson, new ReplaceOptions { IsUpsert = true });
                            time = matrix[i, 0];
                            (bson, findarchive) = await ReadOne(_id, time);
                        }
                        var hh = $"{time:HH}";
                        var mm = $"{time:mm}";
                        var ss = $"{time:ss}";
                        if (!bson.Contains(hh)) bson[hh] = new BsonDocument();
                        if (!((BsonDocument)bson[hh]).Contains(mm)) bson[hh][mm] = new BsonDocument();
                        if (!((BsonDocument)bson[hh][mm]).Contains(ss)) ((BsonDocument)bson[hh][mm]).Add(ss, matrix[i, j + 1]);
                        else bson[hh][mm][ss] = matrix[i, j + 1];
                    }
                    await client.GetDatabase(device).GetCollection<BsonDocument>(CN_ARCHIVE).ReplaceOneAsync(findarchive, bson, new ReplaceOptions { IsUpsert = true });

                    var ts = $"{time:yyyy-MM-dd HH:mm:ss}";
                    var value = matrix[size - 1, j + 1];
                    var upsnapshot = new UpdateDefinitionBuilder<BsonDocument>().Set("_id", _id).Set("time", ts).Set("value", (object)value);
                    var findsnapshot = new FilterDefinitionBuilder<BsonDocument>().Eq("_id", _id);
                    await client.GetDatabase(device).GetCollection<BsonDocument>(CN_SNAPSHOT).UpdateOneAsync(findsnapshot, upsnapshot, new UpdateOptions { IsUpsert = true });
                }
            }
        }
    }

    public override async Task<List<(string Tag, DateTime Time, double Value)>> SnapshotAsync(string device, List<string> tags)
    {
        var keys = tags.Distinct().Select(tag => $"^{tag}$").ToList();
        var pattern = $"/{string.Join("|", keys)}/i";
        var script = new JsonCommand<BsonDocument>($"{{ find:'{CN_SNAPSHOT}', batchSize:{tags.Count}, filter:{{ _id:{pattern} }} }}");
        var cmd = await client.GetDatabase(device).RunCommandAsync(script);
        var items = (BsonArray)cmd["cursor"]["firstBatch"];
        var data = new List<(string Tag, DateTime Time, double Value)>();
        foreach (BsonDocument bson in items)
        {
            if (bson.Contains("time") && bson.Contains("value"))
            {
                data.Add((bson["_id"].AsString, DateTime.Parse(bson["time"].AsString), ReadValue(bson["value"])));
            }
        }
        return data;
    }

    private async Task<(DateTime Time, double? Value)> ReadLastOne(string device, string tag, string type, DateTime date, int digits = 6)
    {
        var keys = $"^{tag}#{date.ToString(TS_DATE_FMT)}$";
        var pattern = $"/{keys}/i";
        var script = new JsonCommand<BsonDocument>($"{{ find:'{CN_ARCHIVE}', filter:{{ _id:{pattern} }}, batchSize:1 }}");
        var cmd = await client.GetDatabase(device).RunCommandAsync(script);

        if (cmd["ok"] == 1)
        {
            var items = (BsonArray)cmd["cursor"]["firstBatch"];
            foreach (BsonDocument bson in items)
            {
                var day = bson["date"].AsString;
                var hour = bson.Elements.Where(x => x.Name != "_id" && x.Name != "date").OrderBy(x => x.Name).LastOrDefault();
                if (hour != null)
                {
                    var min = ((BsonDocument)bson[hour.Name]).Elements.OrderBy(x => x.Name).LastOrDefault();
                    if (min != null)
                    {
                        var sec = ((BsonDocument)bson[hour.Name][min.Name]).Elements.OrderBy(x => x.Name).LastOrDefault();
                        if (sec != null)
                        {
                            var v = type == "DI" ? ReadValueDigital(sec.Value) : ReadValue(sec.Value, digits);
                            var t = sec.Name.Length > 2 ? DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}.{sec.Name[2..]}") : DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}");
                            return (t, v);
                        }
                    }
                }
            }
        }

        return (DateTime.MinValue, default(double?));
    }
    public override async Task<List<(DateTime Time, double Value)>> HistoryAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6, int ms = 1000)
    {
        var jArray = new List<(DateTime Time, double Value)>();
        var days = new List<DateTime>();
        var time = begin.Date;
        var exist = false;
        do { days.Add(time); time = time.AddDays(1); } while (time < end);
        var type = (await GetTagType(device, tag)) ?? "AI";
        var keys = days.Select(day => $"^{tag}#{day.ToString(TS_DATE_FMT)}$").ToList();
        var pattern = $"/{string.Join("|", keys)}/i";
        var script = new JsonCommand<BsonDocument>($"{{ find:'{CN_ARCHIVE}', filter:{{ _id:{pattern} }}, batchSize:{days.Count} }}");
        var cmd = await client.GetDatabase(device).RunCommandAsync(script);
        if (cmd["ok"] == 1)
        {
            var items = (BsonArray)cmd["cursor"]["firstBatch"];
            double? first = null;
            double? last = null;
            foreach (BsonDocument bson in items)
            {
                var day = bson["date"].AsString;
                foreach (var hour in bson)
                {
                    if (!hour.Value.IsBsonDocument) continue;
                    foreach (var min in (BsonDocument)hour.Value)
                    {
                        if (!min.Value.IsBsonDocument) continue;
                        foreach (var sec in (BsonDocument)min.Value)
                        {
                            var v = type == "DI" ? ReadValueDigital(sec.Value) : ReadValue(sec.Value, digits);
                            if (double.IsNaN(v)) continue;
                            var t = sec.Name.Length > 2 ? DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}.{sec.Name[2..]}") : DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}");
                            if (t < begin) { first = v; continue; }
                            if (t > end) { last = jArray.Any() ? jArray[^1].Value : first ?? v; exist = true; break; };
                            jArray.Add((t, v));
                        }
                        if (exist) break;
                    }
                    if (exist) break;
                }
            }
            if (!jArray.Any())
            {
                if (first == null) first = (await ReadLastOne(device, tag, type, begin.AddDays(-1), digits)).Value;
                jArray.Add((begin, first ?? last ?? double.NaN));
                jArray.Add((end, last ?? first ?? double.NaN));
            }
            if (jArray[0].Time != begin)
            {
                if (first == null) first = (await ReadLastOne(device, tag, type, begin.AddDays(-1), digits)).Value;
                jArray.Insert(0, (begin, first ?? jArray[0].Value));
            }
            if (jArray[^1].Time != end)
            {
                jArray.Add((end, last ?? jArray[^1].Value));
            }
        }
        return jArray.Fill(begin, end, ms); ;
    }
    public override async Task<List<(DateTime Time, double Value)>> ArchiveAsync(string device, string tag, DateTime begin, DateTime end, int digits = 6)
    {
        var jArray = new List<(DateTime Time, double Value)>();
        var days = new List<DateTime>();
        var time = begin.Date;
        var exist = false;
        do { days.Add(time); time = time.AddDays(1); } while (time < end);
        var type = (await GetTagType(device, tag)) ?? "AI";
        var keys = days.Select(day => $"^{tag}#{day.ToString(TS_DATE_FMT)}$").ToList();
        var pattern = $"/{string.Join("|", keys)}/i";
        var script = new JsonCommand<BsonDocument>($"{{ find:'{CN_ARCHIVE}', filter:{{ _id:{pattern} }}, batchSize:{days.Count} }}");
        var cmd = await client.GetDatabase(device).RunCommandAsync(script);
        if (cmd["ok"] == 1)
        {
            var items = (BsonArray)cmd["cursor"]["firstBatch"];
            double? first = null;
            double? last = null;
            foreach (BsonDocument bson in items)
            {
                var day = bson["date"].AsString;
                foreach (var hour in bson)
                {
                    if (!hour.Value.IsBsonDocument) continue;
                    foreach (var min in (BsonDocument)hour.Value)
                    {
                        if (!min.Value.IsBsonDocument) continue;
                        foreach (var sec in (BsonDocument)min.Value)
                        {
                            var v = type == "DI" ? ReadValueDigital(sec.Value) : ReadValue(sec.Value, digits);
                            if (double.IsNaN(v)) continue;
                            var t = sec.Name.Length > 2 ? DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}.{sec.Name[2..]}") : DateTime.Parse($"{day} {hour.Name}:{min.Name}:{sec.Name[0..2]}");
                            if (t < begin) { first = v; continue; }
                            if (t > end) { last = v; exist = true; break; };
                            jArray.Add((t, v));
                        }
                        if (exist) break;
                    }
                    if (exist) break;
                }
            }
        }
        return jArray;
    }

    public override async Task<List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit, DateTime? modifyTime)>> PointsAsync(string device, string keywords = "")
    {
        if (keywords?.StartsWith("/") == true) keywords = keywords[1..];
        if (keywords?.EndsWith("/") == true) keywords = keywords[0..^1];
        if (keywords?.EndsWith("/i") == true) keywords = keywords[0..^2];

        var jArray = new List<(string _id, string type, string desc, string unit, double? down, double? up, DateTime? @modifyTime)>();
        var filter = new FilterDefinitionBuilder<BsonDocument>().Regex(x => x["_id"], new BsonRegularExpression($"/{keywords}/i"));
        var items = client.GetDatabase(device).GetCollection<BsonDocument>(CN_POINT).Find(filter).Project(new BsonDocument()
        {
            ["_id"] = 1,
            ["type"] = 1,
            ["unit"] = 1,
            ["desc"] = 1,
            ["downlimit"] = 1,
            ["uplimit"] = 1,
            ["@modifyTime"] = 1,
        }).ToList();

        Func<BsonDocument, string, double?> getdouble = (row, field) => row.TryGetValue(field, out var value) && !value.IsBsonNull ? (value.IsDouble ? value.AsNullableDouble : double.Parse(value.AsString)) : default;
        Func<BsonDocument, string, string> getstring = (row, field) => row.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsString : string.Empty;
        Func<BsonDocument, string, DateTime?> gettime = (row, field) => row.TryGetValue(field, out var value) && !value.IsBsonNull ? DateTime.Parse(value.ToString()) : default(DateTime?);

        foreach (var item in items)
        {
            try
            {
                jArray.Add((getstring(item, "_id"), getstring(item, "type"), getstring(item, "desc"), getstring(item, "unit"), getdouble(item, "downlimit"), getdouble(item, "uplimit"), gettime(item, "@modifyTime")));
            }
            catch (System.Exception ex)
            {
                throw new Exception(item.ToJson(), ex);
            }
        }

        return jArray;
    }

    public override async Task InitializeAsync(string device, List<(string Tag, string Type, string Desc, string Unit, double? Downlimit, double? Uplimit)> measurements)
    {
        var collection = client.GetDatabase(device).GetCollection<BsonDocument>(CN_POINT);
        foreach (var p in measurements)
        {
            var find = new FilterDefinitionBuilder<BsonDocument>().Eq("_id", p.Tag);
            var update = new UpdateDefinitionBuilder<BsonDocument>().Set("_id", p.Tag).Set("type", p.Type).Set("desc", p.Desc).Set("unit", p.Unit).Set("downlimit", p.Downlimit).Set("uplimit", p.Uplimit).Set("@modifyTime", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await collection.FindOneAndUpdateAsync(find, update, new FindOneAndUpdateOptions<BsonDocument, BsonDocument> { IsUpsert = true });
        }
    }

    private double ReadValue(BsonValue value, int digits = 6)
    {
        if (value == null || value.IsBsonNull) return double.NaN;

        if (value.IsDouble) return Math.Round(value.AsDouble, digits);

        if (value.IsInt32) return value.AsInt32;

        if (value.IsInt64) return value.AsInt64 > int.MaxValue || value.AsInt64 < int.MinValue ? value.AsInt64 : (int)value.AsInt64;

        if (value.IsString) return double.TryParse(value.AsString, out var v) ? Math.Round(v, digits) : double.NaN;

        if (value.IsBoolean) return value.AsBoolean ? 1 : 0;

        return double.NaN;
    }

    private int ReadValueDigital(BsonValue value)
    {
        var val = ReadValue(value, 0);
        return double.IsNaN(val) ? 0 : (int)val;
    }

    private async Task<string?> GetTagType(string device, string tag)
    {
        try
        {
            var filter = new FilterDefinitionBuilder<BsonDocument>().Regex(x => x["_id"], new BsonRegularExpression($"/^{tag}$/i"));
            var cmd = await client.GetDatabase(device).RunCommandAsync(new JsonCommand<BsonDocument>($"{{ find:'{CN_POINT}', filter:{{ _id:/^{tag}$/i }}, limit:1, projection:{{ type:1 }} }}"));
            return (int)cmd["ok"] == 1 ? cmd["cursor"]?["firstBatch"]?[0]?["type"]?.AsString : default;
        }
        catch (System.Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"WARNING: -->> {device} -> {(string.IsNullOrEmpty(tag) ? "tag is empty" : $"{tag} is not exist")}");
            Console.ResetColor();
            return string.Empty;
        }
    }
}
