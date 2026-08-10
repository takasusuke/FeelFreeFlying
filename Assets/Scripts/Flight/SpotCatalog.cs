using System;
using System.Collections.Generic;
using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 見どころの一覧（docs/m3-plan.md §4）。**実行時は読むだけ。**
    ///
    /// 位置表（<see cref="TileCatalog"/>）と同じ扱いにする。抽出はEditor側で終わっていて、
    /// 実行時に建物の属性を数え直すことはしない——都市が増えても実行時の仕事は変わらない。
    ///
    /// **名前を持たない**（→ m3-plan.md §1.2、`CLAUDE.md` 不変条件4）。
    /// 実在建築物の名称は出さず、種類と距離だけを表示する。
    /// </summary>
    [Serializable]
    public sealed class SpotCatalog
    {
        /// <summary>都市ごとに1つ。既定の都市名は<see cref="DefaultCity"/>。</summary>
        public const string ResourcePrefix = "spots-";

        public const string DefaultCity = "shinjuku";

        [SerializeField] private List<Spot> spots = new List<Spot>();

        public IReadOnlyList<Spot> Spots => spots;

        public static SpotCatalog Load(string city = DefaultCity)
        {
            var asset = Resources.Load<TextAsset>(ResourcePrefix + city);
            if (asset == null)
            {
                Debug.LogWarning($"[SpotCatalog] Resources/{ResourcePrefix}{city}.json がありません。" +
                                 "見どころは表示されません（M3: 見どころを抽出する を実行してください）。");
                return new SpotCatalog();
            }

            return JsonUtility.FromJson<SpotCatalog>(asset.text) ?? new SpotCatalog();
        }

        public void Replace(IEnumerable<Spot> entries) => spots = new List<Spot>(entries);

        /// <summary>どのルールで採られたか（→ m3-plan.md §2）。**表示に使う語はここだけで決める。**</summary>
        public enum SpotKind
        {
            /// <summary>R1 周りより明らかに高い建物。</summary>
            Height,

            /// <summary>R2 住宅の海に浮かぶ別用途の建物。</summary>
            Usage,

            /// <summary>R3 低いのに広い屋根。</summary>
            Roof,

            /// <summary>R4 建物が無い塊（公園・操車場）。</summary>
            Void,

            /// <summary>R5 地形の高み。</summary>
            Terrain,
        }

        [Serializable]
        public sealed class Spot
        {
            /// <summary>位置から作る識別子。**建物名ではない**（→ m3-plan.md §1.2）。</summary>
            public string id;

            /// <summary>`height` / `usage` / `roof` / `void` / `terrain`。</summary>
            public string kind;

            /// <summary>光の柱の根元。建物なら屋上、空白・地形なら地面。</summary>
            public Vector3 position;

            /// <summary>見どころの広がり (m)。近づいたと判定する距離にも使う。</summary>
            public float radius;

            /// <summary>ルール内での強さ 0〜1。**順位付けにしか使わない。**</summary>
            public float score;

            public SpotKind Kind => kind switch
            {
                "height" => SpotKind.Height,
                "usage" => SpotKind.Usage,
                "roof" => SpotKind.Roof,
                "void" => SpotKind.Void,
                _ => SpotKind.Terrain,
            };

            /// <summary>画面に出す短い名札。**固有名詞を含めない。**</summary>
            public string Label => Kind switch
            {
                SpotKind.Height => "高い建物",
                SpotKind.Usage => "まわりと違う建物",
                SpotKind.Roof => "大きな屋根",
                SpotKind.Void => "ひらけた場所",
                _ => "高台",
            };
        }
    }
}
