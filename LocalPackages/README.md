# LocalPackages

`Packages/manifest.json`がローカルtarballとして参照するパッケージを置く場所。
**tgz本体はgit管理外**（`.gitignore`）。サイズが大きく、公式リリースから再取得できるため。

## PLATEAU SDK for Unity

`manifest.json`の参照:

```
"com.synesthesias.plateau-unity-sdk": "file:../LocalPackages/PLATEAU-SDK-for-Unity-v4.3.0.0.tgz"
```

**クローン直後はこのファイルが無いのでUnityがパッケージを解決できない。** 先に取得する。

```
gh release download v4.3.0 --repo Project-PLATEAU/PLATEAU-SDK-for-Unity --pattern "*.tgz" --dir LocalPackages
```

取得後、サイズが `799,009,840` バイトであることを確認する（**転送が途中で切れていても
ファイルは生成される**。壊れたtgzをUnityに渡すと `zlib: unexpected end of file` になる）。

```
gzip -t LocalPackages/PLATEAU-SDK-for-Unity-v4.3.0.0.tgz && echo OK
```

## なぜGit URL方式ではないのか

`manifest.json`にGitHubのURLを書く方式も公式に用意されているが、実測で **10〜17MB/分**
しか出ず、LFSを含めた取得に1時間以上かかった。tgzは同じ公式リリース成果物なので中身は同じ。

バージョンを上げる時は、新しいtgzを取得し、`manifest.json`のファイル名を書き換える。
