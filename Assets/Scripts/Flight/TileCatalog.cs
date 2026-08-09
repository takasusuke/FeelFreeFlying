using System;
using System.Collections.Generic;
using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// タイルの位置を書き出した表（docs/m2-plan.md §4）。
    ///
    /// **実行時にメッシュコードから座標を計算しない。** 緯度経度→Unity座標の変換は
    /// PLATEAU SDKの仕事で、それを実行ファイルに持ち込みたくない。
    /// 取り込んだ直後に**実際の描画範囲を測って**この表に書き、実行時はそれを読むだけにする。
    /// タイルを作り直せば表も作り直されるので、ずれようがない。
    /// </summary>
    [Serializable]
    public sealed class TileCatalog
    {
        public const string ResourcePath = "tile-catalog";

        [SerializeField] private List<TileEntry> tiles = new List<TileEntry>();

        public IReadOnlyList<TileEntry> Tiles => tiles;

        public static TileCatalog Load()
        {
            var asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset == null)
            {
                Debug.LogError($"[TileCatalog] Resources/{ResourcePath}.json がありません。" +
                               "先に M2: 街をタイルに割って取り込む を実行してください。");
                return new TileCatalog();
            }

            return JsonUtility.FromJson<TileCatalog>(asset.text) ?? new TileCatalog();
        }

        public void Replace(IEnumerable<TileEntry> entries)
        {
            tiles = new List<TileEntry>(entries);
        }

        [Serializable]
        public sealed class TileEntry
        {
            public string sceneName;

            /// <summary>遠景用の軽いシーン（→ docs/m2-plan.md §4.6）。無ければ空。</summary>
            public string farSceneName;

            public string gridCode;
            public Vector3 center;
            public Vector3 size;

            public bool HasFar => !string.IsNullOrEmpty(farSceneName);

            /// <summary>水平面での矩形。高さは見ない（上空にいても真下のタイルは要る）。</summary>
            public float HorizontalDistanceTo(Vector3 position)
            {
                float dx = Mathf.Max(0f, Mathf.Abs(position.x - center.x) - size.x * 0.5f);
                float dz = Mathf.Max(0f, Mathf.Abs(position.z - center.z) - size.z * 0.5f);
                return Mathf.Sqrt(dx * dx + dz * dz);
            }
        }
    }
}
