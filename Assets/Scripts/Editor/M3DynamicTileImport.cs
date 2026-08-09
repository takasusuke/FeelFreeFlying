using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PLATEAU.CityImport.AreaSelector;
using PLATEAU.CityImport.Config;
using PLATEAU.Dataset;
using PLATEAU.DynamicTile;
using PLATEAU.Editor.DynamicTile;
using PLATEAU.PolygonMesh;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// SDKの動的タイル（Addressables）で都市を取り込む（docs/m0-plan.md §6）。
    ///
    /// **自前のストリーミングを作る前に、既にあるもので足りるかを確かめる。**
    /// 要件§7はカリング・LOD・ストリーミングを前提に組むことを求めているが、
    /// いま測っているのは2km四方をまるごとシーンに置いた状態でしかない。
    ///
    /// **制約: タイル化するとメッシュ粒度は地域単位に固定される**（SDKがそう作られている）。
    /// 建物単位でしかできないこと——ランドマークだけ実写テクスチャ、LOD2の穴埋め、
    /// M3の見どころ抽出——と正面からぶつかる。だから「動くか」だけでなく
    /// **「何を諦めることになるか」**を測る。
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M3DynamicTileImport.Import
    /// </summary>
    public static class M3DynamicTileImport
    {
        private const string DatasetPath = "Data/Plateau/13104_shinjuku-ku_pref_2025_citygml_1_op";
        private const string OutputPath = "Data/Plateau/dynamic-tiles";
        private const string ScenePath = "Assets/Scenes/M3DynamicTile.unity";

        private static readonly string[] GridCodes =
        {
            "53394525", "53394526",
            "53394535", "53394536",
        };

        private const int CoordinateZoneId = 9;

        [MenuItem("Tools/FeelFreeFlying/M3: 動的タイルで取り込む")]
        public static void Import() => _ = RunAsync(exitWhenDone: false);

        public static void ImportFromCommandLine() => _ = RunAsync(exitWhenDone: true);

        private static async Task RunAsync(bool exitWhenDone)
        {
            try
            {
                string datasetFullPath = Path.GetFullPath(DatasetPath);
                string outputFullPath = Path.GetFullPath(OutputPath).Replace('\\', '/');
                Directory.CreateDirectory(outputFullPath);

                // **先に保存しておく。** 未保存だとSDKがダイアログを出し、バッチモードで止まる
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);

                var datasetConfig = new DatasetSourceConfigLocal(datasetFullPath);
                using GridCodeList gridCodes = GridCodeList.CreateFromGridCodesStr(GridCodes);

                var config = CityImportConfig.CreateWithAreaSelectResult(
                    new AreaSelectResult(
                        new ConfigBeforeAreaSelect(datasetConfig, CoordinateZoneId),
                        gridCodes,
                        AreaSelectResult.ResultReason.Confirm));

                foreach (var pair in config.PackageImportConfigDict.ForEachPackagePair)
                {
                    PredefinedCityModelPackage package = pair.Key;
                    var packageConfig = pair.Value;

                    bool wanted = package == PredefinedCityModelPackage.Building ||
                                  package == PredefinedCityModelPackage.Relief;
                    packageConfig.ImportPackage = packageConfig.ImportPackage && wanted;
                    if (!packageConfig.ImportPackage) continue;

                    packageConfig.DoSetAttrInfo = false;  // 属性は壊れる（→ §3.1）
                    packageConfig.DoSetMeshCollider = false;
                    packageConfig.IncludeTexture = package == PredefinedCityModelPackage.Relief;

                    // タイル化では粒度は地域単位に固定される。ここで指定しても上書きされるが、
                    // **何が起きているかを読めるように明示しておく**
                    packageConfig.MeshGranularity = MeshGranularity.PerCityModelArea;
                }

                config.DynamicTileImportConfig.ImportType = ImportType.DynamicTile;
                config.DynamicTileImportConfig.OutputPath = outputFullPath;

                Debug.Log($"[M3Tile] 開始: 出力先={outputFullPath}");

                var importer = new ImportToDynamicTile(null);
                bool succeeded = await importer.ExecAsync(config, new CancellationTokenSource().Token);

                if (!succeeded)
                {
                    Debug.LogError("[M3Tile] 動的タイルの生成に失敗しました。");
                    if (exitWhenDone) EditorApplication.Exit(1);
                    return;
                }

                var manager = UnityEngine.Object.FindFirstObjectByType<PLATEAUTileManager>();
                Debug.Log(
                    $"[M3Tile] 完了: TileManager={(manager != null ? "あり" : "なし")} / " +
                    $"カタログ={manager?.CatalogPath}");

                EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);

                long bytes = DirectorySize(outputFullPath);
                Debug.Log($"[M3Tile] タイルの容量: {bytes / 1024f / 1024f:F1} MB / 保存={ScenePath}");

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M3Tile] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        private static long DirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }
            return total;
        }
    }
}
