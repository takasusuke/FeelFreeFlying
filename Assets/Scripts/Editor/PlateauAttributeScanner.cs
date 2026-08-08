using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PLATEAU.CityGML;
using PLATEAU.CityInfo;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M0 §3.1 の確認用。シーン上のPLATEAU都市モデルを全走査し、
    /// **どの属性キーがどれだけ入っているか**をCSVに出す。
    ///
    /// これを人手（インスペクタで1棟ずつ確認）でやると見落とすうえ、
    /// メッシュ粒度を変えて比較する時に前回との差が分からなくなる。
    ///
    /// 見たいのは3点:
    ///   1. 用途・建築年・高さに相当するキーが実際に入っているか（M3の抽出ルールの前提）
    ///   2. その出現率。1割しか入っていない属性は抽出ルールに使えない
    ///   3. **メッシュ粒度「地域単位」でも建物単位の属性が引けるか**
    ///      （引けないなら「軽いが見どころを自動抽出できない」が確定する）
    /// </summary>
    public static class PlateauAttributeScanner
    {
        private const string OutputDirName = "docs/m0";
        private const int SampleValueCount = 5;
        private const int DistinctValueCap = 500;

        [MenuItem("Tools/FeelFreeFlying/M0: PLATEAU属性を走査してCSV出力")]
        public static void Scan()
        {
            // PLATEAU.CityGML にも Object 型があるため完全修飾する
            var groups = UnityEngine.Object.FindObjectsByType<PLATEAUCityObjectGroup>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (groups.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "PLATEAU属性の走査",
                    "シーンに PLATEAUCityObjectGroup が見つかりません。\n" +
                    "先に3D都市モデルをインポートしてください（docs/m0-plan.md §3）。",
                    "OK");
                return;
            }

            var keyStats = new Dictionary<string, KeyStat>();
            var groupRows = new List<GroupRow>(groups.Length);
            int totalCityObjects = 0;

            try
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    PLATEAUCityObjectGroup group = groups[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "PLATEAU属性の走査", $"{group.name} ({i + 1}/{groups.Length})",
                            (i + 1) / (float)groups.Length))
                    {
                        Debug.LogWarning("走査を中断しました。");
                        return;
                    }

                    var row = new GroupRow
                    {
                        Name = group.name,
                        Granularity = group.Granularity.ToString(),
                        Lod = group.Lod,
                    };

                    foreach (CityObjectList.CityObject cityObject in EnumerateAll(group))
                    {
                        row.CityObjectCount++;
                        totalCityObjects++;

                        int attributeCount = Collect(cityObject.AttributesMap, "", keyStats);
                        row.AttributeCountTotal += attributeCount;
                        if (attributeCount > 0) row.WithAttributes++;
                    }

                    groupRows.Add(row);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string directory = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".", OutputDirName);
            Directory.CreateDirectory(directory);

            string keysPath = Path.Combine(directory, "plateau-attribute-keys.csv");
            string groupsPath = Path.Combine(directory, "plateau-city-object-groups.csv");

            File.WriteAllText(keysPath, BuildKeyCsv(keyStats, totalCityObjects), Encoding.UTF8);
            File.WriteAllText(groupsPath, BuildGroupCsv(groupRows), Encoding.UTF8);

            Debug.Log(
                $"[PLATEAU属性走査] グループ {groups.Length} / 地物 {totalCityObjects} / 属性キー {keyStats.Count}\n" +
                $"  {keysPath}\n  {groupsPath}");

            EditorUtility.RevealInFinder(keysPath);
        }

        /// <summary>ルートと子を再帰的にすべて返す。</summary>
        private static IEnumerable<CityObjectList.CityObject> EnumerateAll(PLATEAUCityObjectGroup group)
        {
            CityObjectList list = group.CityObjects;
            if (list?.rootCityObjects == null) yield break;

            var stack = new Stack<CityObjectList.CityObject>(list.rootCityObjects);
            while (stack.Count > 0)
            {
                CityObjectList.CityObject current = stack.Pop();
                if (current == null) continue;
                yield return current;

                if (current.Children == null) continue;
                foreach (CityObjectList.CityObject child in current.Children) stack.Push(child);
            }
        }

        /// <summary>
        /// 属性を再帰的に集計する。入れ子(AttributeSet)は "親/子" のキーに展開する
        /// （SDKの TryGetValueWithSlash と同じ表記なので、そのまま取得コードに使える）。
        /// </summary>
        private static int Collect(
            CityObjectList.Attributes attributes, string prefix, Dictionary<string, KeyStat> stats)
        {
            if (attributes == null) return 0;

            int count = 0;
            foreach (KeyValuePair<string, CityObjectList.Attributes.Value> pair in attributes)
            {
                string key = string.IsNullOrEmpty(prefix) ? pair.Key : prefix + "/" + pair.Key;
                CityObjectList.Attributes.Value value = pair.Value;
                if (value == null) continue;

                if (value.Type == AttributeType.AttributeSet)
                {
                    count += Collect(value.AttributesMapValue, key, stats);
                    continue;
                }

                count++;

                if (!stats.TryGetValue(key, out KeyStat stat))
                {
                    stat = new KeyStat();
                    stats[key] = stat;
                }
                stat.Record(value);
            }

            return count;
        }

        private static string BuildKeyCsv(Dictionary<string, KeyStat> stats, int totalCityObjects)
        {
            var keys = new List<string>(stats.Keys);
            keys.Sort((a, b) => stats[b].Count.CompareTo(stats[a].Count));

            var csv = new StringBuilder();
            csv.AppendLine("key,count,coverage_percent,types,distinct_values,samples,numeric_min,numeric_max");

            foreach (string key in keys)
            {
                KeyStat stat = stats[key];
                float coverage = totalCityObjects > 0 ? stat.Count * 100f / totalCityObjects : 0f;

                csv.Append(Escape(key)).Append(',');
                csv.Append(stat.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(coverage.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(Escape(string.Join("|", stat.Types))).Append(',');
                csv.Append(stat.DistinctLabel).Append(',');
                csv.Append(Escape(stat.SampleLabel)).Append(',');
                csv.Append(stat.NumericMinLabel).Append(',');
                csv.AppendLine(stat.NumericMaxLabel);
            }

            return csv.ToString();
        }

        private static string BuildGroupCsv(List<GroupRow> rows)
        {
            var csv = new StringBuilder();
            csv.AppendLine("gameobject,granularity,lod,city_objects,with_attributes,attributes_per_object");

            foreach (GroupRow row in rows)
            {
                float perObject = row.CityObjectCount > 0
                    ? row.AttributeCountTotal / (float)row.CityObjectCount
                    : 0f;

                csv.Append(Escape(row.Name)).Append(',');
                csv.Append(Escape(row.Granularity)).Append(',');
                csv.Append(row.Lod.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(row.CityObjectCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(row.WithAttributes.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.AppendLine(perObject.ToString("F1", CultureInfo.InvariantCulture));
            }

            return csv.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private sealed class GroupRow
        {
            public string Name;
            public string Granularity;
            public int Lod;
            public int CityObjectCount;
            public int WithAttributes;
            public int AttributeCountTotal;
        }

        private sealed class KeyStat
        {
            public int Count;

            private readonly SortedSet<string> types = new SortedSet<string>();
            private readonly List<string> samples = new List<string>(SampleValueCount);
            private readonly HashSet<string> distinct = new HashSet<string>();
            private bool distinctOverflowed;
            private bool hasNumeric;
            private double numericMin;
            private double numericMax;

            public IEnumerable<string> Types => types;

            public string DistinctLabel => distinctOverflowed
                ? $"{DistinctValueCap}+"
                : distinct.Count.ToString(CultureInfo.InvariantCulture);

            public string SampleLabel => string.Join(" | ", samples);

            public string NumericMinLabel =>
                hasNumeric ? numericMin.ToString("G", CultureInfo.InvariantCulture) : "";

            public string NumericMaxLabel =>
                hasNumeric ? numericMax.ToString("G", CultureInfo.InvariantCulture) : "";

            public void Record(CityObjectList.Attributes.Value value)
            {
                Count++;
                types.Add(value.Type.ToString());

                string text = value.StringValue ?? "";
                if (samples.Count < SampleValueCount && !samples.Contains(text)) samples.Add(text);

                if (!distinctOverflowed)
                {
                    distinct.Add(text);
                    if (distinct.Count >= DistinctValueCap) distinctOverflowed = true;
                }

                if (!TryGetNumeric(value, out double number)) return;

                if (!hasNumeric)
                {
                    hasNumeric = true;
                    numericMin = number;
                    numericMax = number;
                    return;
                }
                if (number < numericMin) numericMin = number;
                if (number > numericMax) numericMax = number;
            }

            private static bool TryGetNumeric(CityObjectList.Attributes.Value value, out double number)
            {
                switch (value.Type)
                {
                    case AttributeType.Integer:
                        number = value.IntValue;
                        return true;
                    case AttributeType.Double:
                        number = value.DoubleValue;
                        return true;
                    default:
                        // Measure（"12.5" のような計測値）やDateも数値として扱えることがあるので試す
                        return double.TryParse(
                            value.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                }
            }
        }
    }
}
