using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルが正しく揃っているかを確かめる（docs/m2-plan.md §2 の「7. 検証」）。
    ///
    /// **「動いた」では足りない**（→ §0）。パイプラインと呼ぶ以上、
    /// 途中で失敗したことが後から分かる必要がある。
    ///
    /// ここで見るのは**人が目で見ても気づきにくい壊れ方**に絞る。
    /// 実際、4枚のタイルが全部原点に重なっていた不具合（→ §4.2）は
    /// シーンを開いても「街がある」ようにしか見えず、位置表を見て初めて分かった。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2Verify.Run
    /// </summary>
    public static class M2Verify
    {
        /// <summary>3次メッシュ1枚は約1km。中心がこれより近い2枚は、原点がずれている疑いがある。</summary>
        private const float MinimumTileSpacingMeters = 500f;

        /// <summary>CityGMLから数えた建物の一覧（`M2AttributeExport`の出力）。</summary>
        private const string AttributeCsv = "Data/Plateau/attributes/buildings.csv";

        [MenuItem("Tools/FeelFreeFlying/M2: タイルを検証する")]
        public static void RunFromMenu() => Check();

        public static void Run()
        {
            EditorApplication.Exit(Check() ? 0 : 1);
        }

        /// <summary>問題が無ければtrue。**取り込みの直後にも呼ぶ**ので、ここでは終了しない。</summary>
        public static bool Check()
        {
            var problems = new List<string>();

            TileCatalog catalog = LoadCatalog(problems);
            if (catalog != null)
            {
                CheckScenesExist(catalog, problems);
                CheckRegisteredInBuild(catalog, problems);
                CheckTilesDoNotOverlap(catalog, problems);
                CheckBuildingCount(catalog, problems);
                CheckAttributesMatch(catalog, problems);
            }

            if (problems.Count == 0)
            {
                Debug.Log($"[M2Verify] 問題なし。タイル {catalog?.Tiles.Count ?? 0} 枚。");
                return true;
            }

            foreach (string problem in problems) Debug.LogError($"[M2Verify] {problem}");
            Debug.LogError($"[M2Verify] {problems.Count} 件の問題があります。");
            return false;
        }

        private static TileCatalog LoadCatalog(List<string> problems)
        {
            var asset = Resources.Load<TextAsset>(TileCatalog.ResourcePath);
            if (asset == null)
            {
                problems.Add($"位置表 Resources/{TileCatalog.ResourcePath}.json がありません。" +
                             "M2: 街をタイルに割って取り込む を実行してください。");
                return null;
            }

            TileCatalog catalog = JsonUtility.FromJson<TileCatalog>(asset.text);
            if (catalog == null || catalog.Tiles.Count == 0)
            {
                problems.Add("位置表が空です。");
                return null;
            }

            return catalog;
        }

        private static void CheckScenesExist(TileCatalog catalog, List<string> problems)
        {
            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                string path = M2TilePipeline.TileScenePath(entry.gridCode);
                if (!File.Exists(path)) problems.Add($"{entry.gridCode}: シーン {path} がありません。");
            }
        }

        /// <summary>
        /// **登録していないシーンは実行時に読めない。**
        ///
        /// ただし登録はビルドの直前に各ビルドスクリプトが行い、
        /// `ProjectSettings/EditorBuildSettings.asset`は**タイルを指したままcommitしない**
        /// （タイル自体がgit管理外なので、cloneした先で存在しないシーンを指すことになる）。
        /// よってここで登録されていないこと自体は失敗ではなく、注意にとどめる。
        /// </summary>
        private static void CheckRegisteredInBuild(TileCatalog catalog, List<string> problems)
        {
            var registered = EditorBuildSettings.scenes
                .Where(entry => entry.enabled)
                .Select(entry => entry.path)
                .ToHashSet();

            var missing = catalog.Tiles
                .Select(entry => M2TilePipeline.TileScenePath(entry.gridCode))
                .Where(path => !registered.Contains(path))
                .ToList();

            if (missing.Count == 0) return;

            Debug.LogWarning(
                $"[M2Verify] Build Settingsに未登録のタイルが {missing.Count} 枚あります。" +
                "ビルドスクリプトが直前に登録するので、そのままビルドして構いません。");
        }

        /// <summary>
        /// タイルが同じ場所に重なっていないか。
        /// **取り込みの原点がタイルごとにずれると、全部が原点に集まる**（→ m2-plan.md §4.2）。
        /// </summary>
        private static void CheckTilesDoNotOverlap(TileCatalog catalog, List<string> problems)
        {
            IReadOnlyList<TileCatalog.TileEntry> tiles = catalog.Tiles;

            for (int i = 0; i < tiles.Count; i++)
            {
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    Vector3 a = tiles[i].center;
                    Vector3 b = tiles[j].center;
                    float distance = new Vector2(a.x - b.x, a.z - b.z).magnitude;

                    if (distance < MinimumTileSpacingMeters)
                    {
                        problems.Add(
                            $"{tiles[i].gridCode} と {tiles[j].gridCode} の中心が {distance:F0} m しか離れていません。" +
                            "取り込みの原点がタイルごとにずれている疑いがあります（→ m2-plan.md §4.2）。");
                    }
                }
            }
        }

        /// <summary>
        /// タイルに散らばった建物の合計が、CityGMLの棟数と合うか。
        /// **分割で建物を落としていないこと**の確認。属性表が無ければ黙って飛ばす。
        /// </summary>
        private static void CheckBuildingCount(TileCatalog catalog, List<string> problems)
        {
            if (!File.Exists(AttributeCsv))
            {
                Debug.Log($"[M2Verify] {AttributeCsv} が無いので棟数の照合は飛ばします。");
                return;
            }

            int expected = File.ReadLines(AttributeCsv).Count() - 1; // 見出し行を除く
            int actual = 0;

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                string path = M2TilePipeline.TileScenePath(entry.gridCode);
                if (!File.Exists(path)) continue;

                // シーンを開かずに数える。**4枚開くと数分かかる**ため、
                // 保存された名前（bldg_...）を数えるだけで足りる
                actual += CountBuildingsInSceneFile(path);
            }

            if (expected > 0 && actual != expected)
            {
                problems.Add($"建物の合計が合いません。CityGML {expected} 棟に対してタイル合計 {actual} 棟。");
            }
            else
            {
                Debug.Log($"[M2Verify] 建物 {actual} 棟（CityGML {expected} 棟）。");
            }
        }

        /// <summary>
        /// タイルの建物の名前が、属性表のgmlIdと対応しているか。
        ///
        /// **M3の前提。** 見どころスポットは建物ごとの属性（用途・高さ・階数）から計算で決める
        /// （`CLAUDE.md` 不変条件7）。タイルに割った建物と属性表が名前で繋がらなくなると、
        /// M3が始められない。取り込み方を変えた時に**静かに壊れる**種類の依存なので、ここで見る。
        /// </summary>
        private static void CheckAttributesMatch(TileCatalog catalog, List<string> problems)
        {
            if (!File.Exists(AttributeCsv)) return;

            var known = new HashSet<string>(StringComparer.Ordinal);
            bool header = true;

            foreach (string line in File.ReadLines(AttributeCsv))
            {
                if (header) { header = false; continue; }

                int comma = line.IndexOf(',');
                known.Add(comma > 0 ? line.Substring(0, comma) : line);
            }

            int checkedNames = 0;
            int missing = 0;
            string firstMissing = null;

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                string path = M2TilePipeline.TileScenePath(entry.gridCode);
                if (!File.Exists(path)) continue;

                foreach (string name in BuildingNamesInSceneFile(path))
                {
                    checkedNames++;
                    if (known.Contains(name)) continue;

                    missing++;
                    firstMissing ??= name;
                }
            }

            if (missing > 0)
            {
                problems.Add($"属性表に無い建物が {missing}/{checkedNames} 棟あります（例: {firstMissing}）。" +
                             "M3の見どころ抽出が建物と結びつけられません。");
            }
            else
            {
                Debug.Log($"[M2Verify] 属性表と対応: {checkedNames} 棟すべて。");
            }
        }

        /// <summary>
        /// シーンのYAMLからGameObjectだけを数える。
        /// **`m_Name`を素朴に数えると二重になる**——メッシュもシーンに埋め込まれていて
        /// 同じ`bldg_`という名前を持つため。UnityのYAMLは`--- !u!<クラスID> &<ID>`で
        /// 区切られ、GameObjectのクラスIDは1。
        /// </summary>
        private static int CountBuildingsInSceneFile(string path)
        {
            int count = 0;
            foreach (string _ in BuildingNamesInSceneFile(path)) count++;
            return count;
        }

        private static IEnumerable<string> BuildingNamesInSceneFile(string path)
        {
            const string classMarker = "--- !u!";
            bool inGameObject = false;

            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(classMarker, StringComparison.Ordinal))
                {
                    string rest = line.Substring(classMarker.Length);
                    int space = rest.IndexOf(' ');
                    string classId = space > 0 ? rest.Substring(0, space) : rest;
                    inGameObject = classId == "1";
                    continue;
                }

                if (!inGameObject) continue;

                int index = line.IndexOf("m_Name:", StringComparison.Ordinal);
                if (index < 0) continue;

                string name = line.Substring(index + "m_Name:".Length).Trim();
                inGameObject = false; // 名前は1つだけ

                if (name.StartsWith("bldg_", StringComparison.Ordinal)) yield return name;
            }
        }
    }
}
