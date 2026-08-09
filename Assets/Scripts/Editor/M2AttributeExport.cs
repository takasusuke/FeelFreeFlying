using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PLATEAU.CityGML;
using PLATEAU.Interop;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// CityGMLから属性を直接読み出してCSVに書き出す（docs/m0-plan.md §3.1 の回避策）。
    ///
    /// **SDKのインポート経由では属性を取れない。** 属性を有効にすると
    /// MessagePackのシリアライズが `MissingMethodException` で落ち、メッシュの配置ごと中断する
    /// （原因はSDK同梱の古い `System.Runtime.CompilerServices.Unsafe.dll`）。
    ///
    /// **CityGMLの読み取りAPIはMessagePackを通らない**ので、そちら経由なら属性は取れる。
    /// どのみちM3の見どころ抽出は「属性を計算して使う」ので、SDKのコンポーネントとして
    /// 4千個のGameObjectに載せるより、**gmlIDを鍵にした表**にしておくほうが扱いやすい。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2AttributeExport.Export
    /// </summary>
    public static class M2AttributeExport
    {
        private const string DatasetPath = "Data/Plateau/13104_shinjuku-ku_pref_2025_citygml_1_op";
        private const string OutputDir = "Data/Plateau/attributes";

        /// <summary>
        /// 取り込むタイルと**同じ範囲**を見る（`M2TilePipeline.GridCodesInUse`）。
        ///
        /// **別々に持つと必ずずれる。** 実際、タイルを9枚に広げた時に属性表が4枚ぶんのまま残り、
        /// 26,559棟のうち17,765棟が表に無い状態になった（`M2Verify`が検出）。
        /// </summary>
        private static string[] GridCodes => M2TilePipeline.GridCodesInUse;

        /// <summary>M3の抽出ルールで使いたい属性。**入れ子はスラッシュ区切りで潰す。**</summary>
        private static readonly string[] WantedKeys =
        {
            "bldg:usage",
            "bldg:yearOfConstruction",
            "bldg:measuredHeight",
            "bldg:storeysAboveGround",
            "bldg:storeysBelowGround",
            "bldg:class",
            "bldg:name",
        };

        [MenuItem("Tools/FeelFreeFlying/M2: CityGMLから属性を書き出す")]
        public static void Export() => Run(exitWhenDone: true);

        /// <summary>取り込みの続きとして呼ぶ入口。**ここでは終了しない。**</summary>
        public static void ExportAll() => Run(exitWhenDone: false);

        private static void Run(bool exitWhenDone)
        {
            string datasetFullPath = Path.GetFullPath(DatasetPath);
            var gmlFiles = FindBuildingGmlFiles(datasetFullPath);

            if (gmlFiles.Count == 0)
            {
                Debug.LogError($"[M2Attr] 対象のGMLが見つかりません: {datasetFullPath}");
                if (exitWhenDone) EditorApplication.Exit(1);
                return;
            }

            var keyCounts = new Dictionary<string, int>();
            var rows = new List<string[]>();
            var seenIds = new HashSet<string>();
            int buildings = 0;

            // 形状は要らない。**属性だけ読むので ignoreGeometries を立てる**（読み込みが桁違いに速い）
            var parserParams = new CitygmlParserParams(false, false, false, true);

            foreach (string gmlPath in gmlFiles)
            {
                Debug.Log($"[M2Attr] 読み込み: {Path.GetFileName(gmlPath)}");

                using CityModel model = CityGml.Load(gmlPath, parserParams);

                foreach (CityObject cityObject in model.RootCityObjects)
                {
                    foreach (CityObject target in Descendants(cityObject))
                    {
                        if (target.Type != CityObjectType.COT_Building) continue;

                        // 同じ建物が親子の両方から拾える。**gmlIDで重複を落とす**
                        if (!seenIds.Add(target.ID)) continue;

                        buildings++;

                        // **キーの大文字小文字はデータ側で揺れる**（bldg:measuredHeight が
                        // measuredheight で来る）。大小を無視して引けるようにする
                        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Flatten(target.NativeAttributesMap, string.Empty, flat, keyCounts);

                        rows.Add(BuildRow(target.ID, flat));
                    }
                }
            }

            Directory.CreateDirectory(OutputDir);
            WriteAttributesCsv(rows);
            WriteKeysCsv(keyCounts, buildings);

            Debug.Log(
                $"[M2Attr] 建物 {buildings} 件 / 属性キー {keyCounts.Count} 種類 → " +
                $"{Path.GetFullPath(OutputDir)}");
        }

        private static List<string> FindBuildingGmlFiles(string datasetFullPath)
        {
            string buildingDir = Path.Combine(datasetFullPath, "udx", "bldg");
            if (!Directory.Exists(buildingDir)) return new List<string>();

            return Directory.GetFiles(buildingDir, "*.gml")
                .Where(path => GridCodes.Any(code => Path.GetFileName(path).StartsWith(code)))
                .OrderBy(path => path)
                .ToList();
        }

        private static IEnumerable<CityObject> Descendants(CityObject root)
        {
            yield return root;
            foreach (CityObject child in root.CityObjectDescendantsDFS) yield return child;
        }

        /// <summary>
        /// 入れ子の属性を「親キー/子キー」に潰す。
        /// PLATEAUの属性は AttributeSet で何段にもなるので、そのままでは表にできない。
        /// </summary>
        private static void Flatten(NativeAttributesMap map, string prefix,
            IDictionary<string, string> destination, IDictionary<string, int> keyCounts)
        {
            if (map == null) return;

            foreach (string key in map.Keys)
            {
                NativeAttributeValue value = map[key];
                string path = string.IsNullOrEmpty(prefix) ? key : $"{prefix}/{key}";

                if (value.Type == AttributeType.AttributeSet)
                {
                    Flatten(value.AsAttrSet, path, destination, keyCounts);
                    continue;
                }

                destination[path] = value.AsString;

                keyCounts.TryGetValue(path, out int count);
                keyCounts[path] = count + 1;
            }
        }

        private static string[] BuildRow(string gmlId, IDictionary<string, string> flat)
        {
            var row = new string[WantedKeys.Length + 1];
            row[0] = gmlId;

            for (int i = 0; i < WantedKeys.Length; i++)
            {
                row[i + 1] = flat.TryGetValue(WantedKeys[i], out string value) ? value : string.Empty;
            }

            return row;
        }

        private static void WriteAttributesCsv(List<string[]> rows)
        {
            var csv = new StringBuilder();
            csv.AppendLine("gmlId," + string.Join(",", WantedKeys));

            foreach (string[] row in rows)
            {
                csv.AppendLine(string.Join(",", row.Select(Escape)));
            }

            File.WriteAllText(Path.Combine(OutputDir, "buildings.csv"), csv.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// **どのキーがどれだけ入っているかの一覧。** 出現率の低い属性は抽出ルールに使えないので、
        /// M3の設計はこの表を見てから決める。
        /// </summary>
        private static void WriteKeysCsv(Dictionary<string, int> keyCounts, int buildings)
        {
            var csv = new StringBuilder();
            csv.AppendLine("key,count,ratio");

            foreach (KeyValuePair<string, int> pair in keyCounts.OrderByDescending(pair => pair.Value))
            {
                float ratio = buildings > 0 ? pair.Value / (float)buildings : 0f;
                csv.AppendLine(
                    $"{Escape(pair.Key)},{pair.Value}," +
                    ratio.ToString("F3", CultureInfo.InvariantCulture));
            }

            File.WriteAllText(Path.Combine(OutputDir, "keys.csv"), csv.ToString(), Encoding.UTF8);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Contains(',') || value.Contains('"')
                ? '"' + value.Replace("\"", "\"\"") + '"'
                : value;
        }
    }
}
