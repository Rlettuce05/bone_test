# Phase 2：楕円体領域判定の実装・検証記録

実装日：2026-11-18。要求の正本は `VRC_骨の口保持ギミック_設計書.md` の2.3節・課題2・Phase 2。

**実装とローカル検査は完了。Unity/UdonSharpコンパイル、ClientSim、VRChat本体でのPhase 2受け入れ試験は未実施。** 以下の期待結果は実機の検証済み結果ではない。

Phase 1は、ClientSimの頭部追従と、VRChat本体の手持ちアバターでbeta=0による概ねの口位置追従をユーザーが確認し、暫定完了とした。パラメータ最適化は保留する。今回の変更で推定式(1a)(1b)は変更しない。

## 1. 調査結果と変更ファイル

| 項目 | 確認結果 |
|---|---|
| Unity | 2022.3.22f1（`ProjectSettings/ProjectVersion.txt`） |
| VRChat SDK | Base / Worlds 3.10.6（VCCの固定バージョン、導入済みpackage.jsonも確認） |
| UdonSharp / ClientSim | Worlds 3.10.6同梱版 |
| Git開始点 | `8497120`。コミット・pushは実施しない |
| 既存変更 | `Assets/MouthPositionPhase1Test.unity`、`ProjectSettings/DynamicsManager.asset`、`ProjectSettings/TagManager.asset`に開始時点から変更あり。保持する |

| ファイル | 今回の変更 |
|---|---|
| `Assets/BoneMouthPickup/MouthRegionDetector.cs` | 新規。楕円体判定とローカル可視化 |
| `Assets/BoneMouthPickup/MouthPositionEstimator.cs` | 同期更新メソッドとフレーム番号を追加。推定式・係数の初期値は維持 |
| `Documentation/Phase1_口位置推定_実装検証.md` | 最新のユーザー報告、暫定完了、調整保留を反映 |
| `Documentation/Phase2_楕円体領域判定_実装検証.md` | 本書 |

`.utmp/phase2`には検査用の応答ファイル、代替ランタイム、実行ファイル、結果ログを置いた。Git除外済みの一時成果物であり、Assetsに含めない。Scene、Prefab、Packages、ProjectSettings、既存`.meta`とGUIDは編集しない。U#プログラムアセットの新規作成と再コンパイルはUnity側の未実施作業として残す。Unityがインポート時に作る新規`.meta`、プログラム`.asset`、`Assets/SerializedUdonPrograms`の更新は実行に必要な生成物であり、手書きでは作成・置換しない。

## 2. 判定と公開出力

`MouthRegionDetector`はInspectorで指定された推定器とBitePointを参照し、毎`PostLateUpdate`に次を計算する。

```text
u = inverse(mouthRotation) * (bitePoint.position - mouthPosition)
r_i = max(0.005, baseRadii_i * (1 + radiusScaleCoefficients_i * (avatarScale - 1)))
D = (u_x / r_x)^2 + (u_y / r_y)^2 + (u_z / r_z)^2
isBitePointInside = D <= 1
```

頭部ローカル軸の原点は推定口位置であり、推定器GameObjectのTransformではない。`mouthRotation`はPhase 1のHead Tracking回転。設計式(2)と同値の「割ってから二乗」で計算する。境界を含み、判定用の追加マージンは設けない。

基本半径の試験開始値は`(0.08, 0.07, 0.12)m`、軸別スケール補正係数は`(1,1,1)`。試作から数値のみ採用し、最適化済みの値とは扱わない。係数0の軸は固定、1の軸はスケール比例になる。係数の編集で半径が0・負になっても、実効半径は各軸5mm以上を維持する。NaN・Infinityになった実効半径も5mmへ戻す。Inspector入力自体を書き換える処理はない。

| 公開出力 | 意味 |
|---|---|
| `hasRegionResult` | 今回の評価が有効。領域外でもtrue |
| `isBitePointInside` | 有効な評価でD≤1ならtrue。それ以外はfalse |
| `regionValueD` | 楕円体の判定値D。評価無効時は`float.PositiveInfinity` |
| `bitePointHeadLocal` | u。評価無効時はゼロ |
| `ellipsoidRadii` | 補正後の正の半径[m]。無効時は最後の値を保持（初期値は各軸5mm） |

判定器の通常Inspectorの`Runtime outputs (do not edit)`は観察用。値を手入力せず、後続スクリプトも読み取り用途で使う。これらは同期しない。`isBitePointInside`は領域内という意味だけであり、取得許可ではない。Contact、相対速度、Pickup、所有権、保持・解除、ネットワーク同期は実装していない。

推定器またはBitePointが未指定／破棄、推定器が無効／非アクティブ、口推定が無効、現在フレームの結果が得られない、スケールが非正／非有限、Dが非有限の場合は評価を無効化する。判定器の`OnDisable`も出力を無効化し、デバッグ表示を隠す。BitePointのワールド位置を読むため、Phase 2には「Boneが単位スケール」「BitePointが直下」という保持姿勢計算の前提はない。

## 3. 同一フレームの更新とイベント順序

既存の推定器と判定器を別々の`PostLateUpdate`にするだけでは、同じ実行順序値のBehaviour間で前フレームの推定を読む可能性がある。今回はInspectorやScript Execution Orderの設定に依存させず、以下の呼び出しにした。

```text
推定器.PostLateUpdate → 推定器.RefreshMouthPose
判定器.PostLateUpdate → 推定器.RefreshMouthPose → 有効性・フレーム番号確認 → 領域判定
```

`RefreshMouthPose()`は、そのフレームに有効な推定があれば再利用し、なければ従来の式で計算する。結果に`mouthPoseFrame = Time.frameCount`を付ける。どちらが先でも頭部読み取りは通常1フレーム1回となり、複数の判定器も同じ推定を利用できる。推定器自身の有効性チェックはキャッシュ判定より先に行う。

アバター変更・眼高変更・`OnDisable`では既存の無効化処理に加えてフレーム番号を-1へ戻す。変更通知が同じフレームで届いた場合も、その後の呼び出しで再計算できる。推定式に平滑化・係数変更・キャリブレーションは追加しない。

後続処理が判定器より早く出力を読めば、判定自体は前の評価のままである。後続Phaseでは「推定→領域→後続判定」の呼び出し順を明示する。`RefreshMouthPose`は頭部更新後の`PostLateUpdate`から利用する契約であり、`Update`から先に呼んで早い姿勢を同一フレームのキャッシュへ入れない。推定器の無効化やアバター通知が領域評価の後に起きた場合、領域の公開値がfalseになるのは次の判定器評価時（判定器自身の無効化は`OnDisable`時）。将来の取得処理は取得時にも推定器の有効性と現在の条件を再確認する。

導入済み`UdonSharpBehaviour.cs`の`PostLateUpdate`、`UdonManager.cs`の同イベント呼び出し、コンパイラの`Behaviour.enabled`変換処理を確認した。型を`MouthPositionEstimator`として参照し、通常のC#メソッド呼び出しをUdonSharpに変換させる。[公式UdonSharp仕様](https://creators.vrchat.com/worlds/udon/udonsharp/)にも他のU#クラスのフィールド参照・メソッド呼び出しが記載されている。[公式イベント順序](https://creators.vrchat.com/worlds/udon/event-execution-order/)は環境による変化を明記しており、Unity/Udonでの最終確認は必要。

## 4. Unityでの設定手順（未実施）

1. VCCからUnity 2022.3.22f1で開く。既存作業を保存してから、Phase 1の検証シーンを`Save As`で`MouthRegionPhase2Test.unity`など別名に保存する。元シーンを置換しない。
2. `MouthRegionDetector.cs`をインポートし、C#エラーを確認する。Projectで`Create > VRChat > Udon > Udon C# Program Asset`を選び、`MouthRegionDetector.asset`を作る。`Source Script`に既存の`MouthRegionDetector.cs`を指定する。同じソースのプログラムアセットがすでに生成されていれば再利用し、`Create Script`で同名スクリプトを作らない。
3. 新規プログラムと既存`MouthPositionEstimator.asset`を再コンパイルする。プログラムInspectorのコンパイル操作を使い、Consoleのエラー件数を記録する。`.cs`を置いただけでU#登録が済んだと判断しない。
4. 空のルートGameObject `MouthRegionDebug`を作り、Udon BehaviourのProgram Sourceへ新規プログラムを割り当てる。`Mouth Estimator`へPhase 1の推定器、`Bite Point`へ対象のTransformを指定する。最初は既存の骨を動かさず、下記の専用Probeで幾何試験する。
5. 別のルートにUnity標準Sphere `DebugMouthEllipsoid`を作る。Sphere Colliderを削除し、Rigidbody・VRC Object Sync・Pickupを付けない。透過マテリアルを使うと観察しやすい。Meshは中心が原点・元の直径1の標準Sphereを使う。
6. `Debug Ellipsoid`へこのSphere、`Show Debug Ellipsoid`へONを指定する。位置は口位置、回転は頭部回転、Scaleは`2 * ellipsoidRadii`で毎評価更新される。元のInspector Scaleには依存しない。
7. `Base Radii = (0.08,0.07,0.12)`、`Radius Scale Coefficients = (1,1,1)`を開始値にする。推定器のalpha、beta、オフセットはPhase 1で使った設定を維持する。
8. ClientSimでPlayし、判定器Inspectorを選択して`Runtime outputs`を観察する。Sceneビューを併用する。一人称カメラでは顔の近くのSphereがクリップされる場合がある。

デバッグSphereは**親なしの専用ルート**が必須。子オブジェクトを指定すると可視化を更新しない（親スケールによる楕円体の歪みを避ける）。推定器／判定器／BitePoint自身やそれらの祖先、Phase 1の口マーカーを誤指定しても更新しない。判定は表示の有無と独立して続く。これらの誤設定時には指定先を動かしたり非表示にしたりせず、そのままにする。無関係な他のオブジェクトの誤指定までは識別できないため、専用Sphereを割り当てる。

### 正確な位置を置くためのProbe

検証シーンのPhase 1マーカー`DebugMouthMarker`の子に空の`BitePointProbe`を作り、判定器のBite Pointに指定する。これは幾何試験用で、既存骨の階層は変更しない。**楕円体Sphereの子には置かない**（入力を動かす誤参照として可視化が拒否される）。

口マーカーがScale `(0.02,0.02,0.02)`、親なしの場合、ProbeのLocal Positionを`u / 0.02`にすれば頭部ローカル変位uになる。口マーカーの表示をONにして、任意でそのMesh Rendererだけを無効にする。GameObjectを無効化しない。マーカーのScaleが異なる場合は各軸の実際のScaleで割る。Probeを見たい場合はColliderなしの小さなSphereを子に付ける。

## 5. ClientSim手動試験（すべて未実施）

入力を変更したら1フレーム以上進めてから結果を読む。半径はInspectorの`Ellipsoid Radii`を使い、眼高設定をs=1と決めつけない。表の数値例はs=1、初期半径、口マーカーScale=0.02の場合。

| 操作 | 期待結果 |
|---|---|
| Probe Local Positionを(0,0,0) | `hasRegionResult=true`、D=0、inside=true。楕円体の中心と一致 |
| X軸の±(4,0,0)、Y軸の±(0,3.5,0)、Z軸の±(0,0,6) | uの各成分が±実効半径、D≈1。計算されたD≤1のときtrue |
| 各軸境界位置を0.99倍 | D≈0.9801、true（6方向すべて） |
| 各軸境界位置を1.01倍 | D≈1.0201、false。ただし`hasRegionResult=true`（6方向すべて） |
| Probeを(2.8,2.45,4.2) | 各成分が半径の0.7倍、D≈1.47、false。箱型判定ではないこと |
| 内側・外側のProbeで頭を左右／上下／斜めへ回転し移動 | uとDが概ね同じで、楕円体の向き・中心も追従。頭部座標へ正しく逆回転されること |
| `Show Debug Ellipsoid`をOFF/ON、参照を一時的に外す | 表示だけが消える／戻る。Dとinsideは継続して正しく計算される |
| 推定器のUdon Behaviourを無効化、次にGameObjectを無効化 | 次の判定器評価でfalse、D=+Infinity、楕円体非表示。再有効化後は次の評価で復帰 |
| 判定器のGameObjectを無効化／再有効化 | `OnDisable`で無効化・非表示、再開時に再評価 |
| BitePoint／推定器の参照を外す | false、D=+Infinity、楕円体非表示。例外なし |
| 基本半径を0や負、補正係数を負にして非正の半径を作る | 対象軸の実効半径が0.005m以上。除算エラーなし。試験後に設定を戻す |

浮動小数点の座標差分・逆回転により、数学上ちょうど境界の点でもDが1の直上／直下になる場合がある。境界検査では表示丸め前のDに対する`<=1`を確認し、0.99倍／1.01倍で内外の切り替えも確認する。実装へ許容誤差やヒステリシスは追加していない。厳密に表現できる座標でのD=1の包含はローカル検査で確認した。

### スケール変更

1. 基準眼高1.6m、係数(1,1,1)で、ClientSimの設定眼高Hを0.8、1.6、3.2mに変えてPlayし直す。実効半径は順に`(0.04,0.035,0.06)`、`(0.08,0.07,0.12)`、`(0.16,0.14,0.24)`m。デバッグScaleはそれぞれ2倍。
2. Probeの頭部ローカル位置を(0.10,0,0)に固定する。上記の順でfalse、false、trueになること。
3. 係数を(0,0.5,1)にし、H=3.2mなら半径が`(0.08,0.105,0.24)`になること。各軸に別々の係数が効くことを確認する。
4. 同じPlay中の眼高変更通知、アバター変更通知と復帰はVRChat本体でも確認する。ClientSimの起動前設定変更だけで通知経路を確認済みとしない。

### 実行順序

推定器と判定器のGameObjectを別々に作り、Play開始順／再有効化順を変えて頭を素早く動かす。Probeを口マーカーの中心に置いた状態でD≈0を維持し、一フレーム前の口位置との差による領域外判定が出ないこと。明示的に両順序を呼び出す検査は代替ランタイムで実施済みだが、Udon VMでのクロスBehaviour呼び出しとIK後の実際のタイミングは本試験で確認する。

## 6. VRChat本体での手動試験（すべて未実施）

1. 検証シーンをSDKの`Build & Test`で起動する。公開アップロードは不要。Console・クライアントのUdonエラーを記録する。
2. Phase 1と同じbeta=0、同じアバターで、鏡またはカメラから楕円体が口位置・頭部回転に追従することを確認する。通常のBitePointを指定した構成でも、領域表示を横切らせる。これは取得試験ではなく、骨が口へ吸着する動作はない。
3. Action Menuからアバタースケールを変更し、各軸の半径が追従すること、元へ戻すと元の大きさへ戻ることを確認する。
4. アバターを切り替えて、古い口位置やスケールが残らないことを確認する。これらの通知はローカルプレイヤーに限る。
5. 実機でbool/Dを観測する場合は検証用U#表示から`hasRegionResult`、`isBitePointInside`、`regionValueD`を読み取る。今回、実機向けHUDは追加していないため、表示の目視だけで数値判定を検証済みとしない。厳密な数値確認はまずClientSim Inspectorで行う。
6. 既存の骨のVRC Pickupを使用して手でPickup／Dropできることを回帰確認する。判定器は骨のTransform・Rigidbody・Pickup設定を更新しない。

## 7. 実施したローカル検査と未実施項目

| 検査 | 結果 |
|---|---|
| 固定SDKのイベント・enabled変換ソース確認 | 実施済み |
| 実SDK参照のC#コンパイル（推定器＋判定器） | 成功、終了コード0 |
| 実装ソースを代替ランタイム上で実行 | 15グループ／134アサーション成功、終了コード0 |
| 推定式の差分確認 | 式(1a)(1b)の計算部分は変更なし。更新入口・フレーム番号のみ追加 |
| Git差分・空白検査 | `git diff --check`と新規ファイルの`git diff --no-index --check`を実施。空白エラーなし |
| 新規U#プログラム登録、Unityインポート・UdonSharpコンパイル | **未実施** |
| Unity実ランタイム・ClientSimの中心／境界／回転／スケール／無効化試験 | **未実施** |
| VRChat Build & Test・実アバター／スケール変更／手Pickup回帰 | **未実施** |

代替ランタイム検査では、本体2本のC#をそのままコンパイルし、Unity/VRCの型だけを検査用の型に置換した。中心、±各軸の境界・内外、斜めの領域外、頭部回転と平行移動、軸別係数、スケール復帰、正の半径、両方のコールバック順、同一フレーム1回の頭部取得、推定器・判定器の無効化、参照欠損、ローカルプレイヤー不在、アバター通知、表示独立性、誤参照、非有限入力を検査した。これは実装の演算・分岐を検証するもので、Unityのイベント配送、Transform内部演算の完全な一致、UdonSharp変換、Udon extern公開、実アバターの挙動を保証しない。

実行結果の末尾：

```text
PASS: 15 groups, 134 assertions (managed substitutes; NOT Unity/Udon runtime).
```

この環境での再実行コマンド（`.utmp/phase2`は一時成果物、別環境への配布物ではない）：

```powershell
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/Tools/netcorerun/netcorerun.exe' `
  'I:/Unity/Editor/2022.3.22f1/Editor/Data/DotNetSdkRoslyn/csc.dll' `
  -noconfig '@.utmp/phase2/Phase2.rsp'
# 上は実SDK参照。下は本体ソース＋代替ランタイムの演算・分岐検査。
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/Tools/netcorerun/netcorerun.exe' `
  'I:/Unity/Editor/2022.3.22f1/Editor/Data/DotNetSdkRoslyn/csc.dll' `
  -noconfig '@.utmp/phase2/Checks.rsp'
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/MonoBleedingEdge/bin/mono.exe' `
  '.utmp/phase2/Phase2Checks.exe'
```

Unity 2022.3.22f1の実行ファイルと当該プロジェクトのEditorプロセスは存在するが、UI操作ツールは`Computer Use native pipe is unavailable ... (os error 2)`で接続できなかった。起動中の編集セッションを閉じたり、同じプロジェクトで別Editorを強制起動したりしていない。既存Editor.logのコンパイル成功記録は今回の新規判定器の検証証拠として扱わない。

## 8. 受け入れ待ちと既知の制約

- 上記Unity設定、UdonSharpコンパイル、ClientSimの試験表、実機の変更通知確認を終えてからPhase 2の動作確認完了とする。
- 半径・スケール係数は暫定値。アバター間の許容範囲や誤取得率の最適化は未実施。
- デバッグ表示は直径1の標準Sphere・親なしを前提とする。表示用マテリアル・HUD・シーンアセットは今回追加していない。
- 領域内という結果だけで取得を開始しない。後続PhaseでContact・接近方向・自由状態などを組み合わせる。

結果追記欄：

| 日付・環境 | 試験項目 | 実測D／bool／目視結果 | Console・Udonエラー | 合否 |
|---|---|---|---|---|
| 未実施 | Unity／ClientSim／VRChat | — | — | 未判定 |
