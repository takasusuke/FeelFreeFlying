using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 見どころを属性と地形から**計算で**選び出す（docs/m3-plan.md §2 の5ルール）。
    ///
    /// **人手で置かない**（`CLAUDE.md` 不変条件7）。手で置くと都市を1つ足すたびに
    /// 同じ作業が発生し、都市追加が止まる。入力は
    /// <see cref="M2AttributeExport"/> の属性表と <see cref="M3BuildingIndex"/> の幾何索引だけ。
    ///
    /// **乱数を使わない**（→ m3-plan.md §3）。同じ入力からは同じスポットが出る。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M3SpotExtract.Build `
    ///     -ffm2tiles-grid &lt;街全体のメッシュコード&gt; -ffm3-city shinjuku
    /// </summary>
    public static class M3SpotExtract
    {
        private const string AttributeCsv = "Data/Plateau/attributes/buildings.csv";
        private const string OutputDir = "Assets/Resources";

        private const string CityArg = "-ffm3-city";
        private const string DensityArg = "-ffm3-density";

        // ---- R1 突出した高さ（→ m3-plan.md §2.1）
        private const float R1MinHeight = 25f;
        private const float R1Radius = 300f;
        private const float R1RatioThreshold = 2.5f;
        private const float R1AbsoluteThreshold = 15f;

        // ---- R2 用途の孤立（→ §2.2）
        private const float R2Radius = 200f;
        private const float R2MaxShare = 0.10f;
        private const float R2MinSizeRank = 0.80f;

        // ---- R3 大きな屋根（→ §2.3）
        private const float R3TopFraction = 0.005f;
        private const float R3MaxHeight = 30f;

        // ---- R4 空白（→ §2.4）
        private const float CellSize = M3BuildingIndex.TerrainCellSize;
        private const float R4EmptyCoverage = 0.02f;
        private const float R4MinArea = 20000f;
        private const float R4MinSurroundingCoverage = 0.10f;

        // ---- R5 地形の高み（→ §2.5）
        private const float R5Radius = 500f;
        private const float R5MinExcess = 20f;

        // ---- 選び方（→ §3）
        private const float MinSeparation = 250f;
        private const float MaxRuleShare = 0.40f;

        /// <summary>地理的な偏りを見る単位 (m)。タイル1枚とだいたい同じ大きさ。</summary>
        private const float DistrictSize = 1000f;

        /// <summary>
        /// 1km²あたり何個まで採るか。
        ///
        /// **m3-plan.md §3の「1km²あたり8個」と「3km四方で約25個」は両立しない**
        /// （3km四方＝9km²なので8個/km²だと72個になる）。実際に飛ぶ密度として
        /// 意図されていたのは後者——3km四方で約25個、つまり平均600mおきに1つ——なので、
        /// **具体的な個数のほうを採った**。`-ffm3-density`で上書きできる。
        /// </summary>
        private const float DefaultSpotsPerKm2 = 2.8f;

        /// <summary>近傍の統計を信用するのに要る最低の棟数。少なすぎる周囲とは比べない。</summary>
        private const int MinNeighbours = 10;

        /// <summary>ルールを採る順番。**固定する**——同じ入力から同じ結果を出すため（→ §3）。</summary>
        private static readonly string[] RuleOrder = { "height", "usage", "roof", "void", "terrain" };

        [MenuItem("Tools/FeelFreeFlying/M3: 見どころを抽出する（タイルを測り直す）")]
        public static void BuildFromMenu() => RunBuild(exitWhenDone: false);

        public static void Build() => RunBuild(exitWhenDone: true);

        /// <summary>
        /// 幾何索引を作り直さずに抽出だけやり直す。
        /// **タイルを開くのは1枚あたり数十秒**かかるので、しきい値を触っている間はこちらを使う。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M3: 見どころを抽出する（測り直さない）")]
        public static void ExtractFromMenu() => RunExtract(exitWhenDone: false);

        public static void Extract() => RunExtract(exitWhenDone: true);

        private static void RunBuild(bool exitWhenDone)
        {
            try
            {
                if (M3BuildingIndex.BuildInProcess() == 0)
                {
                    if (exitWhenDone) EditorApplication.Exit(1);
                    return;
                }

                bool ok = ExtractInProcess();
                if (exitWhenDone) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M3Spot] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        private static void RunExtract(bool exitWhenDone)
        {
            try
            {
                bool ok = ExtractInProcess();
                if (exitWhenDone) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M3Spot] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        public static bool ExtractInProcess()
        {
            List<Building> buildings = LoadBuildings();
            if (buildings == null) return false;

            Bounds area = CityBounds();
            if (area.size.x <= 0f || area.size.z <= 0f)
            {
                Debug.LogError("[M3Spot] 街の範囲が取れません。位置表（tile-catalog.json）を作り直してください。");
                return false;
            }

            var terrain = LoadTerrain();
            var index = new BuildingGrid(buildings);
            var coverage = new CoverageGrid(area, buildings);

            var candidates = new List<Candidate>();
            candidates.AddRange(RuleHeight(buildings, index));
            candidates.AddRange(RuleUsage(buildings, index));
            candidates.AddRange(RuleRoof(buildings));
            candidates.AddRange(RuleVoid(coverage, terrain));
            candidates.AddRange(RuleTerrain(terrain));

            float areaKm2 = area.size.x * area.size.z / 1_000_000f;
            int limit = Mathf.Max(1, Mathf.RoundToInt(ResolveDensity() * areaKm2));

            List<Candidate> selected = Select(candidates, limit, area);
            WriteCatalog(selected);

            ReportCounts(candidates, selected, areaKm2, limit);
            return selected.Count > 0;
        }

        // ------------------------------------------------------------------ ルール

        /// <summary>
        /// R1 突出した高さ。**上位N棟ではなく周囲との差**で採る（→ m3-plan.md §2.1）。
        /// 上位N棟だと新宿駅周辺に全部集まり、「街を見て回る」にならない。
        /// </summary>
        private static IEnumerable<Candidate> RuleHeight(List<Building> buildings, BuildingGrid index)
        {
            foreach (Building building in buildings)
            {
                if (!building.HasHeight || building.Height < R1MinHeight) continue;

                var heights = new List<float>();
                foreach (Building neighbour in index.Query(building.X, building.Z, R1Radius))
                {
                    if (neighbour.HasHeight && neighbour.Id != building.Id) heights.Add(neighbour.Height);
                }

                if (heights.Count < MinNeighbours) continue;

                float median = Median(heights);
                if (median <= 0f) continue;

                // しきい値を1.0に揃えて比べる。**どちらか一方でも超えれば採る**
                float ratio = building.Height / median / R1RatioThreshold;
                float absolute = (building.Height - median) / R1AbsoluteThreshold;
                float margin = Mathf.Max(ratio, absolute);

                if (margin < 1f) continue;

                yield return Candidate.ForBuilding("height", building, Saturate(margin, 3f));
            }
        }

        /// <summary>
        /// R2 用途の孤立。**面積の条件を外さない**（→ §2.2）——
        /// 小さな異物は空から見えないので、見どころにならない。
        /// </summary>
        private static IEnumerable<Candidate> RuleUsage(List<Building> buildings, BuildingGrid index)
        {
            foreach (Building building in buildings)
            {
                if (!building.HasUsage) continue;

                int total = 0;
                int same = 0;
                int smaller = 0;
                int sized = 0;

                foreach (Building neighbour in index.Query(building.X, building.Z, R2Radius))
                {
                    if (neighbour.HasUsage)
                    {
                        total++;
                        if (neighbour.Usage == building.Usage) same++;
                    }

                    if (neighbour.Footprint <= 0f) continue;

                    sized++;
                    if (neighbour.Footprint < building.Footprint) smaller++;
                }

                if (total < MinNeighbours * 3 || sized < MinNeighbours) continue;

                float share = same / (float)total;
                if (share > R2MaxShare) continue;

                float sizeRank = smaller / (float)sized;
                if (sizeRank < R2MinSizeRank) continue;

                // 珍しさと大きさを半々で見る。**どちらかだけでは空から見つけられない**
                float rarity = 1f - share / R2MaxShare;
                float size = (sizeRank - R2MinSizeRank) / (1f - R2MinSizeRank);

                yield return Candidate.ForBuilding("usage", building,
                    Mathf.Clamp01(0.5f * rarity + 0.5f * size));
            }
        }

        /// <summary>
        /// R3 大きな屋根。**高さで上限を切ってR1と役割を分ける**（→ §2.3）。
        /// 切らないと超高層が「大きな屋根」としても採られ、同じ建物が二重に出る。
        /// </summary>
        private static IEnumerable<Candidate> RuleRoof(List<Building> buildings)
        {
            var footprints = buildings.Where(b => b.Footprint > 0f)
                .Select(b => b.Footprint).ToList();

            if (footprints.Count < 100) yield break;

            float threshold = Percentile(footprints, 1f - R3TopFraction);
            if (threshold <= 0f) yield break;

            foreach (Building building in buildings)
            {
                if (building.Footprint < threshold) continue;
                if (building.HasHeight && building.Height > R3MaxHeight) continue;

                // 高さが欠測（-9999）の建物は、外接箱の高さで判断する。
                // **高さが分からない建物を無条件で通すと、超高層が屋根として混ざる**
                if (!building.HasHeight && building.SizeY > R3MaxHeight) continue;

                yield return Candidate.ForBuilding("roof", building,
                    Saturate(building.Footprint / threshold, 3f));
            }
        }

        /// <summary>
        /// R4 空白。**建物の属性を一切使わない**（→ §2.4）ので、どの都市でも同じように効く。
        /// 空から見た時に最も分かりやすいのは、実は建物ではなく空白。
        /// </summary>
        private static IEnumerable<Candidate> RuleVoid(CoverageGrid coverage, TerrainGrid terrain)
        {
            int minCells = Mathf.CeilToInt(R4MinArea / (CellSize * CellSize));
            bool[] core = ErodeEmptyCells(coverage);

            var visited = new bool[coverage.Width * coverage.Height];
            var results = new List<Candidate>();
            int components = 0;
            int tooSmall = 0;
            int onBorder = 0;
            int notSurrounded = 0;

            for (int z = 0; z < coverage.Height; z++)
            {
                for (int x = 0; x < coverage.Width; x++)
                {
                    int start = z * coverage.Width + x;
                    if (visited[start] || !core[start]) continue;

                    List<int> component = Flood(coverage, core, visited, x, z);
                    components++;

                    if (component.Count < minCells) { tooSmall++; continue; }

                    // **街の外まで続いている空白は採らない。** 端に触れている塊は、
                    // 3km四方の外側（データが無いだけの場所）とつながっている
                    if (component.Any(cell => coverage.IsOnBorder(cell))) { onBorder++; continue; }

                    if (SurroundingCoverage(coverage, component) < R4MinSurroundingCoverage)
                    {
                        notSurrounded++;
                        continue;
                    }

                    float sumX = 0f;
                    float sumZ = 0f;
                    foreach (int cell in component)
                    {
                        sumX += coverage.CellCenterX(cell);
                        sumZ += coverage.CellCenterZ(cell);
                    }

                    float centerX = sumX / component.Count;
                    float centerZ = sumZ / component.Count;
                    float areaM2 = component.Count * CellSize * CellSize;

                    results.Add(new Candidate
                    {
                        Kind = "void",
                        Position = new Vector3(centerX, terrain.HeightAt(centerX, centerZ), centerZ),
                        Radius = Mathf.Clamp(Mathf.Sqrt(areaM2 / Mathf.PI), 40f, 300f),
                        Score = Mathf.Clamp01(areaM2 / 100000f),
                    });
                }
            }

            Debug.Log($"[M3Spot]   空白の内訳: 塊 {components} 個 → " +
                      $"狭い {tooSmall} / 街の外へ続く {onBorder} / まわりが市街地でない {notSurrounded} / " +
                      $"採用 {results.Count}");

            return results;
        }

        /// <summary>
        /// 空白の格子を1つ内側へ削る。
        ///
        /// **削らないと街全体が1つの空白になる。** 取り込んでいるのは建築物と土地起伏だけで
        /// 道路が入っていない（→ `M0CityImport`）ので、道路も空白として数えられる。
        /// 道路は街の隅々までつながっているため、公園も操車場も道路経由で1つの塊になり、
        /// **街の端に触れる**という理由で丸ごと落ちる（実際、最初の実装では候補が2個しか出なかった）。
        ///
        /// 8近傍がすべて空白の格子だけを残すと、幅1格子（50m）の道路は消え、
        /// **広がりのある空白だけ**が残る。
        /// </summary>
        private static bool[] ErodeEmptyCells(CoverageGrid coverage)
        {
            var core = new bool[coverage.Width * coverage.Height];
            int empty = 0;

            for (int z = 0; z < coverage.Height; z++)
            {
                for (int x = 0; x < coverage.Width; x++)
                {
                    if (coverage[x, z] >= R4EmptyCoverage) continue;
                    empty++;

                    bool surrounded = true;

                    for (int dz = -1; dz <= 1 && surrounded; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;

                            if (!coverage.TryCell(x + dx, z + dz, out int neighbour) ||
                                coverage.CoverageOf(neighbour) >= R4EmptyCoverage)
                            {
                                surrounded = false;
                                break;
                            }
                        }
                    }

                    if (surrounded) core[z * coverage.Width + x] = true;
                }
            }

            Debug.Log($"[M3Spot]   空白の格子: {empty} 個 → 削った後 {core.Count(cell => cell)} 個 " +
                      $"（全体 {core.Length} 格子）");

            return core;
        }

        /// <summary>
        /// R5 地形の高み。**出ない都市があって構わない**（→ §2.5）。
        /// 新宿は平坦なのでほとんど出ないが、神戸・長崎のような街を足した時に効く。
        /// </summary>
        private static IEnumerable<Candidate> RuleTerrain(TerrainGrid terrain)
        {
            int radiusCells = Mathf.CeilToInt(R5Radius / CellSize);

            foreach ((int cellX, int cellZ, float height) in terrain.Cells)
            {
                var around = new List<float>();

                for (int dz = -radiusCells; dz <= radiusCells; dz++)
                {
                    for (int dx = -radiusCells; dx <= radiusCells; dx++)
                    {
                        if (dx * dx + dz * dz > radiusCells * radiusCells) continue;
                        if (terrain.TryGet(cellX + dx, cellZ + dz, out float sample)) around.Add(sample);
                    }
                }

                if (around.Count < 50) continue;

                float excess = height - Median(around);
                if (excess < R5MinExcess) continue;

                yield return new Candidate
                {
                    Kind = "terrain",
                    Position = new Vector3(
                        (cellX + 0.5f) * CellSize, height, (cellZ + 0.5f) * CellSize),
                    Radius = 120f,
                    Score = Mathf.Clamp01(excess / 60f),
                };
            }
        }

        // ------------------------------------------------------------------ 選び方（§3）

        /// <summary>
        /// **ルールごとに順番に1つずつ**採る（→ m3-plan.md §3）。
        ///
        /// 全候補をスコア順に並べて上から採ると、**候補の少ないルールが1つも入らない**。
        /// 実際、最初の実装では空白（R4）の候補2個がどちらも入らず、
        /// 40%の上限に達した「高い建物」と「まわりと違う建物」だけで枠が埋まった。
        /// 上限は偏りを止めるが、**入ることを保証しない。**
        ///
        /// スコアはルールをまたいで比べられる量ではない（それぞれ別の式で0〜1に丸めただけ）ので、
        /// 横並びに比べること自体をやめて、順番に採る形にした。
        ///
        /// **1km四方あたりの上限**も併せて持つ。250m離すだけでは、
        /// 超高層の集まる一角に10個が250m間隔で並ぶ（実際にそうなった）。
        /// 見どころは街に散っていなければ「見て回る」にならない（→ §2.1）。
        /// </summary>
        private static List<Candidate> Select(List<Candidate> candidates, int limit, Bounds area)
        {
            int perRule = Mathf.Max(1, Mathf.CeilToInt(limit * MaxRuleShare));

            int districts = Mathf.Max(1, Mathf.CeilToInt(area.size.x / DistrictSize)) *
                            Mathf.Max(1, Mathf.CeilToInt(area.size.z / DistrictSize));
            int perDistrict = Mathf.Max(2, Mathf.CeilToInt(limit * 1.5f / districts));
            var districtCount = new Dictionary<(int, int), int>();

            // **並び替えを完全に決める。** スコアが同じ候補の順序が実行ごとに変わると、
            // 250mの間引きでどちらが残るかが変わり、同じ入力から同じ結果が出なくなる
            var queues = RuleOrder
                .Select(kind => new Queue<Candidate>(candidates
                    .Where(candidate => candidate.Kind == kind)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)))
                .ToList();

            var selected = new List<Candidate>();
            var taken = new int[queues.Count];
            bool progressed = true;

            while (selected.Count < limit && progressed)
            {
                progressed = false;

                for (int rule = 0; rule < queues.Count && selected.Count < limit; rule++)
                {
                    if (taken[rule] >= perRule) continue;

                    while (queues[rule].Count > 0)
                    {
                        Candidate candidate = queues[rule].Dequeue();

                        bool tooClose = selected.Any(other =>
                            HorizontalDistance(other.Position, candidate.Position) < MinSeparation);
                        if (tooClose) continue;

                        (int, int) district = DistrictOf(candidate.Position);
                        districtCount.TryGetValue(district, out int inDistrict);
                        if (inDistrict >= perDistrict) continue;

                        selected.Add(candidate);
                        districtCount[district] = inDistrict + 1;
                        taken[rule]++;
                        progressed = true;
                        break;
                    }
                }
            }

            return selected.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// **上限に張り付かない0〜1**。`x / (x + k)` は単調で、いくら大きくなっても1に達しない。
        ///
        /// 最初は「しきい値の4倍で1.0」と切っていたが、中央値9.7mの街では
        /// 100m級の建物がすべて1.00になり、**順位がスコアではなくidの文字列順で決まった**。
        /// 結果として高い建物10個が全部西新宿に並び、街を見て回る形にならなかった。
        /// </summary>
        private static float Saturate(float value, float half) => value / (value + half);

        private static (int, int) DistrictOf(Vector3 position) =>
            (Mathf.FloorToInt(position.x / DistrictSize), Mathf.FloorToInt(position.z / DistrictSize));

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ------------------------------------------------------------------ 出力

        private static void WriteCatalog(List<Candidate> selected)
        {
            var catalog = new SpotCatalog();
            catalog.Replace(selected.Select(candidate => new SpotCatalog.Spot
            {
                id = candidate.Id,
                kind = candidate.Kind,
                position = candidate.Position,
                radius = candidate.Radius,
                score = Mathf.Round(candidate.Score * 1000f) / 1000f,
            }));

            Directory.CreateDirectory(OutputDir);
            string path = $"{OutputDir}/{SpotCatalog.ResourcePrefix}{ResolveCity()}.json";

            File.WriteAllText(path, JsonUtility.ToJson(catalog, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path);

            Debug.Log($"[M3Spot] 見どころ {selected.Count} 個 → {path}");
        }

        /// <summary>
        /// **候補と採用の内訳を必ず出す。** どのルールが効いていないのかは、
        /// 出力されたJSONを見ても分からない（採られなかった候補は残らないため）。
        /// </summary>
        private static void ReportCounts(List<Candidate> candidates, List<Candidate> selected,
            float areaKm2, int limit)
        {
            Debug.Log($"[M3Spot] 街の広さ {areaKm2:F1} km² / 目安 {limit} 個 " +
                      $"（1km²あたり {ResolveDensity():F1} 個・1ルール上限 {Mathf.CeilToInt(limit * MaxRuleShare)} 個）");

            foreach (string kind in RuleOrder)
            {
                int found = candidates.Count(candidate => candidate.Kind == kind);
                int taken = selected.Count(candidate => candidate.Kind == kind);
                Debug.Log($"[M3Spot]   {kind}: 候補 {found} 個 → 採用 {taken} 個");
            }

            // **散っているかどうかを数字で残す。** 「街を見て回る」が成り立つかは、
            // 個数ではなく1km四方あたりの分布で決まる（→ §3）
            var districts = selected.GroupBy(candidate => DistrictOf(candidate.Position)).ToList();
            Debug.Log($"[M3Spot]   1km四方への散らばり: {districts.Count} 区画 / " +
                      $"最多 {(districts.Count > 0 ? districts.Max(group => group.Count()) : 0)} 個");
        }

        // ------------------------------------------------------------------ 入力

        private sealed class Building
        {
            public string Id;
            public float X;
            public float Z;
            public float TopY;
            public float SizeX;
            public float SizeY;
            public float SizeZ;
            public float Footprint;
            public float Height;
            public bool HasHeight;
            public string Usage;

            public bool HasUsage => !string.IsNullOrEmpty(Usage);
        }

        /// <summary>
        /// 属性表と幾何索引を突き合わせる。**両方に居る建物だけ**を扱う——
        /// 片方にしか無い建物は、位置か用途のどちらかが欠けていて判定できない。
        /// </summary>
        private static List<Building> LoadBuildings()
        {
            if (!File.Exists(AttributeCsv))
            {
                Debug.LogError($"[M3Spot] {AttributeCsv} がありません。先に属性を書き出してください。");
                return null;
            }

            if (!File.Exists(M3BuildingIndex.GeometryCsv))
            {
                Debug.LogError($"[M3Spot] {M3BuildingIndex.GeometryCsv} がありません。" +
                               "先に M3: 建物の位置と底面積を測る を実行してください。");
                return null;
            }

            var attributes = LoadAttributes();
            var buildings = new List<Building>();

            string[] lines = File.ReadAllLines(M3BuildingIndex.GeometryCsv);
            int missing = 0;
            int fallbackHeight = 0;

            foreach (string line in lines.Skip(1))
            {
                string[] cells = line.Split(',');
                if (cells.Length < 9) continue;

                string id = cells[0];
                if (!attributes.TryGetValue(id, out (string Usage, float Height) attribute))
                {
                    missing++;
                    attribute = (null, 0f);
                }

                float baseY = ParseFloat(cells[3]);
                float sizeY = ParseFloat(cells[6]);

                // **欠測は極端な数値で入っている**（高さ-9999 → m3-plan.md §1.1）。
                // 属性が使えない時は取り込んだ形の高さで代用する——
                // 実際に画面に出ている建物の高さなので、少なくとも嘘ではない
                bool hasHeight = attribute.Height > 0f && attribute.Height < 1000f;
                float height = hasHeight ? attribute.Height : sizeY;

                if (!hasHeight && sizeY > 0f && sizeY < 1000f)
                {
                    hasHeight = true;
                    fallbackHeight++;
                }

                buildings.Add(new Building
                {
                    Id = id,
                    X = ParseFloat(cells[2]),
                    Z = ParseFloat(cells[4]),
                    TopY = baseY + sizeY,
                    SizeX = ParseFloat(cells[5]),
                    SizeY = sizeY,
                    SizeZ = ParseFloat(cells[7]),
                    Footprint = ParseFloat(cells[8]),
                    Height = height,
                    HasHeight = hasHeight,
                    Usage = attribute.Usage,
                });
            }

            Debug.Log($"[M3Spot] 建物 {buildings.Count} 棟（属性が無い {missing} 棟 / " +
                      $"高さを形から代用した {fallbackHeight} 棟）");

            return buildings;
        }

        private static Dictionary<string, (string Usage, float Height)> LoadAttributes()
        {
            var attributes = new Dictionary<string, (string, float)>(StringComparer.Ordinal);
            string[] lines = File.ReadAllLines(AttributeCsv);
            if (lines.Length < 2) return attributes;

            string[] header = SplitCsv(lines[0]);
            int usageColumn = Array.IndexOf(header, "bldg:usage");
            int heightColumn = Array.IndexOf(header, "bldg:measuredHeight");

            foreach (string line in lines.Skip(1))
            {
                string[] cells = SplitCsv(line);
                if (cells.Length <= Mathf.Max(usageColumn, heightColumn)) continue;

                string usage = usageColumn >= 0 ? cells[usageColumn] : string.Empty;

                // **「不明」は用途ではない。** 2,661棟あるので、これを1つの用途として数えると
                // 「不明の海に浮かぶ不明」が孤立として採られる
                if (usage == "不明") usage = string.Empty;

                float height = heightColumn >= 0 ? ParseFloat(cells[heightColumn]) : 0f;
                attributes[cells[0]] = (usage, height);
            }

            return attributes;
        }

        private static TerrainGrid LoadTerrain()
        {
            var grid = new TerrainGrid();
            if (!File.Exists(M3BuildingIndex.TerrainCsv))
            {
                Debug.LogWarning($"[M3Spot] {M3BuildingIndex.TerrainCsv} がありません。地形のルール（R5）は動きません。");
                return grid;
            }

            foreach (string line in File.ReadAllLines(M3BuildingIndex.TerrainCsv).Skip(1))
            {
                string[] cells = line.Split(',');
                if (cells.Length < 3) continue;
                if (!int.TryParse(cells[0], out int x) || !int.TryParse(cells[1], out int z)) continue;

                grid.Add(x, z, ParseFloat(cells[2]));
            }

            return grid;
        }

        /// <summary>街の範囲。**位置表から取る**——タイルが実際に占めている場所そのもの。</summary>
        private static Bounds CityBounds()
        {
            TileCatalog catalog = TileCatalog.Load();
            if (catalog.Tiles.Count == 0) return new Bounds();

            var bounds = new Bounds(catalog.Tiles[0].center, catalog.Tiles[0].size);
            foreach (TileCatalog.TileEntry entry in catalog.Tiles.Skip(1))
            {
                bounds.Encapsulate(new Bounds(entry.center, entry.size));
            }

            return bounds;
        }

        // ------------------------------------------------------------------ 補助

        private sealed class Candidate
        {
            public string Kind;
            public Vector3 Position;
            public float Radius;
            public float Score;

            /// <summary>
            /// 位置から作る識別子。**建物名もgmlIDも使わない**——
            /// 実在建築物を名指ししない（`CLAUDE.md` 不変条件4）ためと、
            /// 都市データを作り直しても同じ場所なら同じidにするため。
            /// </summary>
            public string Id => $"{Kind}_{Mathf.RoundToInt(Position.x)}_{Mathf.RoundToInt(Position.z)}";

            public static Candidate ForBuilding(string kind, Building building, float score) =>
                new Candidate
                {
                    Kind = kind,
                    // 光の柱は屋上から立てる。**建物の中から立てると柱が見えない**
                    Position = new Vector3(building.X, building.TopY, building.Z),
                    Radius = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(building.Footprint, 1f) / Mathf.PI) + 20f,
                        40f, 200f),
                    Score = score,
                };
        }

        /// <summary>近傍検索のための格子。26,559棟に対して半径200〜300mを何万回も引く。</summary>
        private sealed class BuildingGrid
        {
            private const float Cell = 100f;

            private readonly Dictionary<(int, int), List<Building>> cells =
                new Dictionary<(int, int), List<Building>>();

            public BuildingGrid(IEnumerable<Building> buildings)
            {
                foreach (Building building in buildings)
                {
                    (int, int) key = KeyOf(building.X, building.Z);
                    if (!cells.TryGetValue(key, out List<Building> list))
                    {
                        list = new List<Building>();
                        cells[key] = list;
                    }

                    list.Add(building);
                }
            }

            public IEnumerable<Building> Query(float x, float z, float radius)
            {
                int span = Mathf.CeilToInt(radius / Cell);
                (int cellX, int cellZ) = KeyOf(x, z);
                float squared = radius * radius;

                for (int dz = -span; dz <= span; dz++)
                {
                    for (int dx = -span; dx <= span; dx++)
                    {
                        if (!cells.TryGetValue((cellX + dx, cellZ + dz), out List<Building> list)) continue;

                        foreach (Building building in list)
                        {
                            float ox = building.X - x;
                            float oz = building.Z - z;
                            if (ox * ox + oz * oz <= squared) yield return building;
                        }
                    }
                }
            }

            private static (int, int) KeyOf(float x, float z) =>
                (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));
        }

        /// <summary>
        /// 建物の被覆率を50m格子に落としたもの（R4）。
        /// **中心の格子だけに足さない。** 底面積1万m²の建物を1格子（2,500m²）に押し込むと、
        /// その建物が覆っている隣の格子が「空白」に見える。
        /// </summary>
        private sealed class CoverageGrid
        {
            private readonly float[] values;
            private readonly float originX;
            private readonly float originZ;

            public int Width { get; }
            public int Height { get; }

            public CoverageGrid(Bounds area, IEnumerable<Building> buildings)
            {
                originX = Mathf.Floor(area.min.x / CellSize) * CellSize;
                originZ = Mathf.Floor(area.min.z / CellSize) * CellSize;

                Width = Mathf.Max(1, Mathf.CeilToInt((area.max.x - originX) / CellSize));
                Height = Mathf.Max(1, Mathf.CeilToInt((area.max.z - originZ) / CellSize));

                values = new float[Width * Height];

                foreach (Building building in buildings) Stamp(building);

                for (int i = 0; i < values.Length; i++) values[i] /= CellSize * CellSize;
            }

            public float this[int x, int z] => values[z * Width + x];

            public bool IsOnBorder(int cell)
            {
                int x = cell % Width;
                int z = cell / Width;
                return x == 0 || z == 0 || x == Width - 1 || z == Height - 1;
            }

            public float CellCenterX(int cell) => originX + (cell % Width + 0.5f) * CellSize;

            public float CellCenterZ(int cell) => originZ + (cell / Width + 0.5f) * CellSize;

            public float CoverageOf(int cell) => values[cell];

            public bool TryCell(int x, int z, out int cell)
            {
                cell = z * Width + x;
                return x >= 0 && z >= 0 && x < Width && z < Height;
            }

            private void Stamp(Building building)
            {
                if (building.Footprint <= 0f) return;

                // 外接矩形に対する底面の詰まり具合。L字の建物なら1より小さくなる
                float rect = Mathf.Max(building.SizeX * building.SizeZ, 0.01f);
                float fill = Mathf.Clamp01(building.Footprint / rect);

                float minX = building.X - building.SizeX * 0.5f;
                float maxX = building.X + building.SizeX * 0.5f;
                float minZ = building.Z - building.SizeZ * 0.5f;
                float maxZ = building.Z + building.SizeZ * 0.5f;

                int fromX = Mathf.Clamp(Mathf.FloorToInt((minX - originX) / CellSize), 0, Width - 1);
                int toX = Mathf.Clamp(Mathf.FloorToInt((maxX - originX) / CellSize), 0, Width - 1);
                int fromZ = Mathf.Clamp(Mathf.FloorToInt((minZ - originZ) / CellSize), 0, Height - 1);
                int toZ = Mathf.Clamp(Mathf.FloorToInt((maxZ - originZ) / CellSize), 0, Height - 1);

                for (int z = fromZ; z <= toZ; z++)
                {
                    float cellMinZ = originZ + z * CellSize;
                    float overlapZ = Mathf.Min(maxZ, cellMinZ + CellSize) - Mathf.Max(minZ, cellMinZ);
                    if (overlapZ <= 0f) continue;

                    for (int x = fromX; x <= toX; x++)
                    {
                        float cellMinX = originX + x * CellSize;
                        float overlapX = Mathf.Min(maxX, cellMinX + CellSize) - Mathf.Max(minX, cellMinX);
                        if (overlapX <= 0f) continue;

                        values[z * Width + x] += overlapX * overlapZ * fill;
                    }
                }
            }
        }

        /// <summary>地形の標本（<see cref="M3BuildingIndex"/>が書く50m格子）。</summary>
        private sealed class TerrainGrid
        {
            private readonly Dictionary<(int, int), float> heights = new Dictionary<(int, int), float>();

            public IEnumerable<(int X, int Z, float Height)> Cells =>
                heights.OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2)
                    .Select(pair => (pair.Key.Item1, pair.Key.Item2, pair.Value));

            public void Add(int x, int z, float height) => heights[(x, z)] = height;

            public bool TryGet(int x, int z, out float height) => heights.TryGetValue((x, z), out height);

            /// <summary>その場所の地面の高さ。標本が無ければ0（海抜0mの平地とみなす）。</summary>
            public float HeightAt(float worldX, float worldZ)
            {
                var key = (Mathf.FloorToInt(worldX / CellSize), Mathf.FloorToInt(worldZ / CellSize));
                return heights.TryGetValue(key, out float height) ? height : 0f;
            }
        }

        private static List<int> Flood(CoverageGrid coverage, bool[] core, bool[] visited,
            int startX, int startZ)
        {
            var component = new List<int>();
            var queue = new Queue<(int X, int Z)>();

            queue.Enqueue((startX, startZ));
            visited[startZ * coverage.Width + startX] = true;

            while (queue.Count > 0)
            {
                (int x, int z) = queue.Dequeue();
                component.Add(z * coverage.Width + x);

                foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nextX = x + dx;
                    int nextZ = z + dz;

                    if (!coverage.TryCell(nextX, nextZ, out int cell)) continue;
                    if (visited[cell] || !core[cell]) continue;

                    visited[cell] = true;
                    queue.Enqueue((nextX, nextZ));
                }
            }

            return component;
        }

        /// <summary>
        /// 空白の**まわりが市街地か**を見る（→ m3-plan.md §2.4）。
        /// 街の外へ続く空き地と、街に囲まれた公園を分けるための条件。
        /// </summary>
        private static float SurroundingCoverage(CoverageGrid coverage, List<int> component)
        {
            var inside = new HashSet<int>(component);
            var ring = new HashSet<int>();

            foreach (int cell in component)
            {
                int x = cell % coverage.Width;
                int z = cell / coverage.Width;

                for (int dz = -2; dz <= 2; dz++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (!coverage.TryCell(x + dx, z + dz, out int neighbour)) continue;
                        if (inside.Contains(neighbour)) continue;
                        ring.Add(neighbour);
                    }
                }
            }

            if (ring.Count == 0) return 0f;
            return ring.Sum(cell => coverage.CoverageOf(cell)) / ring.Count;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1
                ? values[middle]
                : (values[middle - 1] + values[middle]) * 0.5f;
        }

        private static float Percentile(List<float> values, float fraction)
        {
            values.Sort();
            int index = Mathf.Clamp(Mathf.FloorToInt(fraction * (values.Count - 1)), 0, values.Count - 1);
            return values[index];
        }

        /// <summary>属性表は用途に「,」を含みうるので、引用符を見て割る。</summary>
        private static string[] SplitCsv(string line)
        {
            var cells = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;

            foreach (char character in line)
            {
                if (character == '"') { quoted = !quoted; continue; }

                if (character == ',' && !quoted)
                {
                    cells.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(character);
            }

            cells.Add(current.ToString());
            return cells.ToArray();
        }

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : 0f;

        private static string ResolveCity() => Argument(CityArg) ?? SpotCatalog.DefaultCity;

        private static float ResolveDensity()
        {
            string value = Argument(DensityArg);
            return value != null && float.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float parsed) && parsed > 0f
                ? parsed
                : DefaultSpotsPerKm2;
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }
    }
}
