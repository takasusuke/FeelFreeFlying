using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 高い建物だけ実写テクスチャを残す（docs/m2-plan.md §2 の「4b. ランドマーク」）。
    ///
    /// M1では街全体が1シーンだったので、その場で高さ順に上位N棟を採れた（`M1LandmarkTextures`）。
    /// タイルに割ると**1枚だけ見ても街全体の順位が分からない**ので、
    /// 順位は**属性表**（`M2AttributeExport`が書く`bldg:measuredHeight`）から決める。
    ///
    /// テクスチャは**シーンに埋め込まれる**ため、実写を載せるには
    /// そのタイルをテクスチャ込みで取り込み直すしかない。
    /// **上位N棟が入っているタイルだけ**やり直して、残りはそのまま使う。
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2Landmarks.Apply `
    ///     -ffm2tiles-grid &lt;街全体のメッシュコード&gt; -fflandmarks 40
    /// </summary>
    public static class M2Landmarks
    {
        private const string AttributeCsv = "Data/Plateau/attributes/buildings.csv";
        private const string CountArg = "-fflandmarks";
        private const int DefaultCount = 40;

        /// <summary>この高さ以下は上位に入っても採らない (m)。M1と同じ基準。</summary>
        private const float MinimumHeight = 40f;

        [MenuItem("Tools/FeelFreeFlying/M2: ランドマークだけ実写テクスチャにする")]
        public static void ApplyFromMenu() => _ = ApplyAsync(exitWhenDone: false);

        public static void Apply() => _ = ApplyAsync(exitWhenDone: true);

        /// <summary>
        /// 取り込み直さずに、**どのタイルが対象になるか**だけ出す。
        /// 実写込みの取り込みは1枚10分規模なので、着手前に規模を知るための入口。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M2: ランドマークの対象タイルを数える")]
        public static void Report()
        {
            HashSet<string> landmarks = SelectLandmarks(out float tallest, out float shortest);
            if (landmarks.Count == 0) return;

            Debug.Log($"[M2Landmark] 上位 {landmarks.Count} 棟: {tallest:F1} m 〜 {shortest:F1} m");

            foreach (KeyValuePair<string, int> pair in CountPerTile(landmarks).OrderByDescending(p => p.Value))
            {
                Debug.Log($"[M2Landmark]   {pair.Key}: {pair.Value} 棟");
            }
        }

        /// <summary>
        /// ランドマークが**実際にテクスチャを持っているか**を数える。
        ///
        /// 「実写のまま残した」とログに出しても、元の建物がテクスチャを持っていなければ
        /// 何も残っていない。**LOD2に無い建物はLOD1で補完している**（→ `m0-plan.md` §3.3）ので、
        /// 郊外のタイルほどテクスチャの無いランドマークが混ざる。
        ///
        ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
        ///     -executeMethod FeelFreeFlying.EditorTools.M2Landmarks.VerifyTextures
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M2: ランドマークのテクスチャを数える")]
        public static void VerifyTextures()
        {
            HashSet<string> landmarks = SelectLandmarks(out _, out _);
            if (landmarks.Count == 0) { EditorApplication.Exit(1); return; }

            int withTexture = 0;
            int without = 0;

            foreach (string tileName in CountPerTile(landmarks).Keys.OrderBy(name => name))
            {
                string scenePath = $"{M2TilePipeline.TileDir}/{tileName}.unity";
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                int tileWith = 0;
                int tileWithout = 0;

                foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!landmarks.Contains(renderer.name)) continue;

                    if (HasTexture(renderer)) tileWith++;
                    else tileWithout++;
                }

                withTexture += tileWith;
                without += tileWithout;

                Debug.Log($"[M2Landmark] {tileName}: テクスチャあり {tileWith} 棟 / 無し {tileWithout} 棟");
            }

            Debug.Log($"[M2Landmark] 合計: テクスチャあり {withTexture} 棟 / 無し {without} 棟");
            EditorApplication.Exit(0);
        }

        private static bool HasTexture(Renderer renderer)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) continue;

                foreach (string property in material.GetTexturePropertyNames())
                {
                    Texture texture = material.GetTexture(property);

                    // SDK同梱の汎用テクスチャはアセットとして存在する。**実写は
                    // シーンに埋め込まれる**ので、アセットパスの有無で見分ける
                    if (texture != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static async Task ApplyAsync(bool exitWhenDone)
        {
            try
            {
                HashSet<string> landmarks = SelectLandmarks(out float tallest, out float shortest);
                if (landmarks.Count == 0)
                {
                    if (exitWhenDone) EditorApplication.Exit(1);
                    return;
                }

                Dictionary<string, int> perTile = CountPerTile(landmarks);
                Debug.Log($"[M2Landmark] 上位 {landmarks.Count} 棟（{tallest:F1} 〜 {shortest:F1} m）が " +
                          $"{perTile.Count} 枚のタイルに散っています");

                string[] allCodes = M2TilePipeline.GridCodesInUse;

                foreach (string tileName in perTile.Keys.OrderBy(name => name))
                {
                    string gridCode = tileName.Replace("Tile_", string.Empty);
                    string scenePath = M2TilePipeline.TileScenePath(gridCode);

                    Debug.Log($"[M2Landmark] {gridCode} を実写込みで取り込み直します（{perTile[tileName]} 棟）");

                    // **テクスチャ込みで取り込む。** 実写はシーンに埋め込まれるので、
                    // 後から足すことができない
                    await M0CityImport.ImportTile(gridCode, scenePath, allCodes, includeTexture: true);

                    int kept = PaintKeepingLandmarks(landmarks);
                    EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);

                    Debug.Log($"[M2Landmark] {gridCode}: 実写のまま残した建物 {kept} 棟");
                }

                // 取り込み直したタイルは当たり判定も位置表も焼き込みも作り直しになる。
                // **外壁は塗り直さない**——ここで残した実写テクスチャが消える
                M2TilePipeline.PrepareTilesInProcess(paintWalls: false);

                if (exitWhenDone) EditorApplication.Exit(M2Verify.Check() ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Landmark] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 属性表から高い順にN棟。**街全体の順位**なので、タイルの中身は見ない。
        /// </summary>
        private static HashSet<string> SelectLandmarks(out float tallest, out float shortest)
        {
            tallest = 0f;
            shortest = 0f;

            if (!File.Exists(AttributeCsv))
            {
                Debug.LogError($"[M2Landmark] {AttributeCsv} がありません。先に属性を書き出してください。");
                return new HashSet<string>();
            }

            string[] lines = File.ReadAllLines(AttributeCsv);
            if (lines.Length < 2) return new HashSet<string>();

            string[] header = lines[0].Split(',');
            int heightColumn = Array.IndexOf(header, "bldg:measuredHeight");
            if (heightColumn < 0)
            {
                Debug.LogError("[M2Landmark] 属性表に bldg:measuredHeight がありません。");
                return new HashSet<string>();
            }

            var candidates = new List<(string Id, float Height)>();

            foreach (string line in lines.Skip(1))
            {
                string[] cells = line.Split(',');
                if (cells.Length <= heightColumn) continue;

                if (!float.TryParse(cells[heightColumn], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float height)) continue;

                if (height < MinimumHeight) continue;
                candidates.Add((cells[0], height));
            }

            // **実写が付く建物だけを候補にする。** LOD1で補った建物は元データにテクスチャが無く、
            // ランドマークに選んでも何も残らない（→ docs/m2-plan.md §4.4）。
            // 記録が無い場合（古いタイル）は絞らない
            HashSet<string> lod2 = LoadLod2Index();
            if (lod2.Count > 0)
            {
                int before = candidates.Count;
                candidates = candidates.Where(pair => lod2.Contains(pair.Id)).ToList();
                Debug.Log($"[M2Landmark] 実写が付く建物に絞り込み: {before} → {candidates.Count} 棟");
            }
            else
            {
                Debug.LogWarning("[M2Landmark] LOD2の記録がありません。テクスチャの無い建物が混ざります" +
                                 "（タイルを取り込み直すと記録されます）。");
            }

            var selected = candidates.OrderByDescending(pair => pair.Height).Take(ResolveCount()).ToList();
            if (selected.Count == 0) return new HashSet<string>();

            tallest = selected[0].Height;
            shortest = selected[^1].Height;

            return new HashSet<string>(selected.Select(pair => pair.Id), StringComparer.Ordinal);
        }

        /// <summary>
        /// LOD2で入った建物の一覧（`M0CityImport`がタイルごとに書く）。
        /// 無ければ空を返す——**古いタイルでも動くようにする**ため、絞り込みは任意にしてある。
        /// </summary>
        private static HashSet<string> LoadLod2Index()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            string directory = Path.Combine(Path.GetDirectoryName(AttributeCsv) ?? string.Empty, "lod2");

            if (!Directory.Exists(directory)) return names;

            foreach (string path in Directory.GetFiles(directory, "*.txt"))
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length > 0) names.Add(line.Trim());
                }
            }

            return names;
        }

        /// <summary>どのタイルに何棟入っているか。**シーンを開かずに名前だけ数える。**</summary>
        private static Dictionary<string, int> CountPerTile(HashSet<string> landmarks)
        {
            var perTile = new Dictionary<string, int>();

            if (!Directory.Exists(M2TilePipeline.TileDir)) return perTile;

            foreach (string path in Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity"))
            {
                var found = new HashSet<string>(StringComparer.Ordinal);

                foreach (string line in File.ReadLines(path))
                {
                    int index = line.IndexOf("m_Name:", StringComparison.Ordinal);
                    if (index < 0) continue;

                    string name = line.Substring(index + "m_Name:".Length).Trim();
                    if (landmarks.Contains(name)) found.Add(name);
                }

                if (found.Count > 0) perTile[Path.GetFileNameWithoutExtension(path)] = found.Count;
            }

            return perTile;
        }

        /// <summary>
        /// ランドマーク以外を様式化に差し替え、**使われなくなったテクスチャをシーンから捨てる**。
        /// 捨てないとビルドに残る（M1で+451MBになった → `m1-plan.md` §6.3）。
        /// </summary>
        private static int PaintKeepingLandmarks(HashSet<string> landmarks)
        {
            var buildings = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.name.StartsWith("bldg_", StringComparison.Ordinal))
                .ToList();

            var kept = buildings.Where(renderer => landmarks.Contains(renderer.name)).ToList();
            var keptSet = new HashSet<MeshRenderer>(kept);
            var cache = new Dictionary<string, Material>();

            foreach (MeshRenderer renderer in buildings)
            {
                if (keptSet.Contains(renderer)) continue;

                Material generic = M1LandmarkTextures.MaterialForHeight(renderer.bounds.size.y, cache);

                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = generic;
                renderer.sharedMaterials = materials;
            }

            int discarded = M1LandmarkTextures.DiscardUnreferencedTextures(kept);
            Debug.Log($"[M2Landmark]   捨てたテクスチャ: {discarded} 枚");

            return kept.Count;
        }

        private static int ResolveCount()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == CountArg && int.TryParse(args[i + 1], out int parsed)) return parsed;
            }
            return DefaultCount;
        }
    }
}
