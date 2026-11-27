# Phase 3：Head Contact検出の実装・検証記録

実装日：2026-11-26。要求の正本は`VRC_骨の口保持ギミック_設計書.md`の課題3・Phase 3と今回の実装要件。

**実装・実SDK参照C#コンパイル・代替ランタイム検査は完了。Unity起動はライセンスエラーで失敗したため、UdonSharpコンパイル、U#プログラム登録、ClientSimとVRChat本体での動作検証は未実施。Phase 3の受け入れは保留。**

Phase 2はユーザーがClientSimで検証し、境界付近の数値誤差はあるが許容できる動作と報告したため、暫定完了とした。個々の実測値・ログの提出は受けていない。Phase 1の推定式、Phase 2の楕円体判定式と`D <= 1`、既存係数は変更していない。

## 1. 環境・変更範囲

| 項目 | 確認結果 |
|---|---|
| Unity | `2022.3.22f1`（ProjectVersion.txt） |
| SDK Base / Worlds | `3.10.6`（vpm-manifest.json、導入済みSDK） |
| UdonSharp / ClientSim | 上記Worlds同梱版 |
| Git開始点 | `a99cb6b`、開始時の作業ツリーはクリーン。コミット・pushなし |
| `Assets/BoneMouthPickup/MouthContactDetector.cs` | 新規。Contact集合の管理、領域とのAND、Inspector・任意UI Textの診断出力 |
| `Assets/BoneMouthPickup/MouthRegionDetector.cs` | `RefreshRegion()`と`regionResultFrame`を追加。既存PostLateUpdateはこの入口を呼ぶ。計算式は変更なし |
| `Documentation/Phase2_楕円体領域判定_実装検証.md` | ユーザー検証結果・暫定完了を記録 |
| `Documentation/Phase3_HeadContact検出_実装検証.md` | 本書 |

骨のTransform、Rigidbody、VRC Pickup、ネットワーク状態は変更しない。速度・方向判定、保持・解除、所有権処理は今回の対象外。`isAcquisitionCandidate`は取得を実行する許可ではなく、Phase 3の候補だけを表す。

Scene、Prefab、Packages、ProjectSettings、既存`.meta`は編集していない。Unityバッチ検証用の一時Editorスクリプトを用意したが、起動失敗後にAssetsから除去し`.utmp/phase3`へ保管した。新規`.meta`やU#プログラムアセットはまだ生成されていない。`.utmp/phase3`の検査ソース・ログはGit除外済みのローカル成果物で、ワールドへ含めない。

## 2. SDK 3.10.6のContact API調査

オンラインの[公式Contacts仕様](https://creators.vrchat.com/common-components/contacts/#udon-access-in-worlds)に加え、固定SDKのソース・DLLメタデータ・IL・Udon externを確認した。オンライン文書の最新仕様だけで利用可否を判断していない。

| API・性質 | 導入済みSDKで確認した内容／採用方法 |
|---|---|
| `OnContactEnter(ContactEnterInfo contactInfo)` / `OnContactExit(ContactExitInfo contactInfo)` | `UdonSharpBehaviour.cs`にvirtualイベントあり。両方をoverride |
| 配送先 | `ContactReceiverUdonEmitter.cs`がReceiverと同一GameObjectの`IUdonBehaviour`へ配送する |
| `contactSender`, `contactReceiver` | `VRC.Dynamics`のProxy。`matchingTags`は`string[]` |
| `ContactSenderProxy == ContactSenderProxy` | SDKの比較演算子がある。有効なProxy同士は内部のContact shape識別子で比較する。別Proxyインスタンスでも同じSenderなら重複登録しない |
| Sender識別子の直接取得 | 内部`contactShapeId`は公開APIではない。`playerId`、Proxyの参照同一性、ハッシュ値をSenderの一意IDとして使わない |
| `sender.isValid` | **参照先Componentが存在するかだけ**。ILはUnity Objectのnull比較で、`enabled`や`activeInHierarchy`を検査しない |
| `sender.usage` / `sender.player` | `isValid`確認後、`DynamicsUsage.Avatar`、有効なplayer、`player.isLocal`をすべて要求 |
| Headタグ | Enterの`matchingTags`に大文字小文字を区別して`"Head"`があることを確認。ReceiverのInspector設定だけには依存しない |
| Receiver照合 | `ContactReceiverProxy != VRCContactReceiver`で指定Receiver以外のイベントを除外 |
| Senderの無効化 | Proxyからavatar側Component／GameObject／enabledは取得できない。SDKのExitを使う。破棄時には毎評価の`isValid`検査でも除去 |
| `CalculateProximity` | 幾何学的な近さであり、有効・接触中Sender一覧のAPIではない。enabledの代わりやHeadの再認証に使わない |

参照したローカル実装：

- `Packages/com.vrchat.worlds/Integrations/UdonSharp/Runtime/UdonSharpBehaviour.cs`
- `Packages/com.vrchat.worlds/Runtime/Udon/Dynamics/ContactReceiverUdonEmitter.cs`
- `Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/WorldDynamicsSetup.cs`
- `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/VRC.Dynamics.dll`
- `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/VRC.SDK3.Dynamics.Contact.dll`
- `Packages/com.vrchat.worlds/Runtime/Udon/External/VRC.Udon.VRCWrapperModules.dll`

最後のDLLでProxy配列の生成・Get・Set、Proxy比較、Receiverとの比較、isValid／usage／player、イベント引数のgetter、Receiverのenabled／gameObjectがUdonに公開されていることを確認した。これはUdonSharpコンパイル成功の代替ではない。

### 無効化検出の限界と試作からの修正

試作の単一boolではA・Bの同時接触中にAが離れるだけでfalseになり、Exit時の`!sender.isValid`による早期returnは破棄済みSenderの状態を残し得る。今回はSender集合に置き換え、無効参照の掃除をExitと毎フレームに行う。

SDKの`ContactBase.OnDisable`はContactManagerからshapeを除去し、Receiverの`OnShapeExit → RemoveCollision`にはExit配送処理がある。ただし**実クライアントでのSender無効化時の配送・順序・遅延は未検証**。Receiver側には集合を直接クリアする内部経路もあるため、全ケースでExitが来ることは前提にしない。自Receiverと自Behaviourの無効化は別途検出する。

**SDKの制約：まだ存在するavatar Senderについて、無効化されているのにExitが届かない場合、公開Proxy APIだけではその不整合を識別できない。** この場合まで残留ゼロを保証済みとはしない。Sender無効化試験を受け入れ条件とし、配送漏れを観測した場合の最小の追加案はReceiverの明示的な再初期化と新規Enter待ち。今回、自動的なReceiver再起動・接触タイムアウト・独自距離判定は導入していない。単に時間切れで消すと正常な静止接触も失われるためである。

## 3. 接触集合・公開出力・更新順序

登録対象は「指定Receiverから届いた、ローカルAvatar由来で、Headが一致する、有効なSender」のみ。配列は8要素から必要に応じ倍増し、容量超過による暗黙の取りこぼしを避ける。同じSenderのEnterは冪等。ExitはそのSenderだけを除去し、重複Exitや未登録SenderのExitで数が負にならない。削除時は末尾を詰める。後から変化し得るExitのタグ・playerを再フィルタして削除を妨げない。

| 状況 | 処理 |
|---|---|
| 毎PostLateUpdate・Enter・Exit | 破棄／無効Proxy、無効player、local AvatarではなくなったSenderを除去 |
| Receiver参照の差し替え／欠損、別GameObject、Receiver component無効 | 接触集合と候補をクリア。ポーリング検出は次の評価時 |
| 検出器／そのGameObjectの無効化・再有効化 | OnDisable／OnEnableで全クリア |
| ローカルアバター変更 | 全クリア、同一フレームのEnterを拒否。変更前のイベントを引き継がない。他人の変更通知は無視 |
| 眼高変更 | 候補と領域スナップショットを無効化。接触集合は維持しSDKのEnter/Exitで更新、次の評価でスケール反映 |
| 明示的な`ResetContacts()` | 全クリア。デバッグや外部の無効化処理との連携用 |

クリア後は新しいEnterが必要。アバター変更と同じフレームに新アバターのEnterが届いた場合も拒否するため、一度離して再接触する必要がある。検出器だけを無効化して再有効化した場合、Receiverが接触を継続しているとEnterが再送されない可能性があり、同様に離脱・再接触する。保存した古い接触を推測で復活させない。

| Inspectorの公開値 | 意味 |
|---|---|
| `isReceiverReady` | 指定Receiverが同一GameObject上で有効、検出器も有効 |
| `hasLocalHeadContact` / `validContactCount` | フィルタを通り、Exitしておらず、現在のProxy/playerが有効な接触の有無／数。SDKの通知に基づく値で、直接の重なり再計測ではない |
| `hasRegionResult` / `isBitePointInside` | 現在フレームの領域評価の有効性／内側。Contactがなくても独立に更新 |
| `isAcquisitionCandidate` | 有効なReceiver・領域評価の下で`hasLocalHeadContact && isBitePointInside` |
| `candidateFrame` | 候補を評価したフレーム番号。イベントによる候補無効化・参照不備などでは-1。評価が有効で候補falseの場合にも現在フレームを記録 |
| `contactEnterEventCount` / `contactExitEventCount` | フィルタ前の配送数。接触数ではない。重複・対象外イベントでも増える |
| 領域判定器の`regionResultFrame` | 有効な領域を計算したフレーム。無効時は-1 |

これらはすべてローカル専用。Inspector値は観察専用で、書き込まない。

```text
MouthContactDetector.PostLateUpdate
  → RefreshCandidate
    → Receiver有効性・Sender集合の掃除
    → MouthRegionDetector.RefreshRegion
      → MouthPositionEstimator.RefreshMouthPose
      → 現在フレームの推定を確認 → 既存の楕円体判定
    → 現在フレームの領域を確認 → Contact AND 領域内
    → 任意UI Text更新
```

Contactイベント内では集合を更新し、候補をfalse／フレーム-1にする。早いHead poseを同一フレームのキャッシュへ入れないため、イベント内から推定器・領域判定器は呼ばない。表示は次のPostLateUpdateで確定する。[公式イベント順序](https://creators.vrchat.com/worlds/udon/event-execution-order/)も環境差を記載しているため、ContactとPostLateUpdateが常に特定順序で届くとは仮定しない。

`RefreshRegion()`は同じフレームでもBitePointを再評価する。推定器の既存フレームキャッシュは維持する。後続Phaseからは頭部更新後に`RefreshCandidate()`を呼び、`candidateFrame == Time.frameCount`を確認して使う。イベント後の未再評価値や、単なる前フレームのtrueを取得に流用しない。別コンポーネントの無効化などは次の評価まで反映されないことがある。

## 4. Unityでの設定手順（未実施）

1. ライセンスが有効なUnity 2022.3.22f1でVCCの当該プロジェクトを開く。既存Phase 2シーン`Assets/MouthPositionPhase2Test.unity`を保存し、必要ならUnityのSave Asで`MouthPositionPhase3Test.unity`へ複製する。元シーンを置換しない。
2. `.cs`のインポートとConsoleのC#エラーを確認する。Projectの`Create > VRChat > Udon > Udon C# Program Asset`で`MouthContactDetector.asset`を作り、Source Scriptに既存の`Assets/BoneMouthPickup/MouthContactDetector.cs`を指定する。同じソースのアセットが自動生成されていればそれを使う。同名ソースをCreate Scriptで重複作成しない。
3. 新規Contactプログラムと既存RegionプログラムをUdonSharpでコンパイルし、Consoleの結果を保存する。C#コンパイルだけで成功と判断しない。新規`.meta`／SerializedUdonProgramsはUnityに生成させ、既存GUIDは保持する。
4. 骨のBitePoint付近に空の`HeadContactReceiver`を作る。幾何テストでは独立した単位スケールのProbeでもよい。**このGameObjectにはVRC Contact Receiverを1つだけ**置き、同じGameObjectにMouthContactDetectorのUdon Behaviourを追加する。通常のUnity ColliderをContactの代わりにしない。
5. ReceiverのContent Typesを`Avatar`のみ、Collision Tagsを正確に`Head`のみとする。Shape TypeはSphere、Radiusは開始例として`0.05m`、Positionは`(0,0,0)`。このサイズは調整対象で、最適化済みではない。Root Transformは空欄なら自GameObject。BitePoint近傍に実際に配置されていることをGizmoで確認する。
6. 世界側Receiverにはavatar用のAllow Self／Allow Others／Local Onlyでローカル限定する設定は使わない。player.isLocalによる絞り込みはスクリプトで行う。Receiver TypeのOnEnterやMin Velocityも設定しない。相対速度の条件はPhase 4の対象。
7. Detectorの`Contact Receiver`に同じGameObjectのReceiver、`Mouth Region Detector`にPhase 2の領域判定器を割り当てる。Contact Receiverが空の場合はStartで同一GameObjectから取得するが、明示設定を推奨。領域判定器のBite Pointには、このReceiverを配置した骨／ProbeのBitePointを指定する。
8. Phase 1のalpha／betaとオフセット、Phase 2の半径・係数を維持する。Phase 3ではアバター改変や個別キャリブレーションは不要。Contactの配置とサイズはワールド制作者の設定。
9. デバッグには通常InspectorのRuntime outputsを使う。VRChat本体でも値を見る場合は、独立したCanvas（World Space、読みやすい位置）にUnity UIの**Legacy Text（UnityEngine.UI.Text、TMPではない）**を置き、`Debug Status Text`へ割り当てる。ReceiverやBitePointの親を表示ON/OFFに使わない。
10. Play／Build & Testで、Contact、領域valid／inside、候補、Enter／Exit件数がそれぞれ別行に表示されることを確認する。表示なしでも判定は動作する。`isAcquisitionCandidate=true`になっても骨は保持されない。

## 5. ClientSimで検証できる範囲

導入済み`WorldDynamicsSetup.cs`はEditor Play開始時にContact初期化とUdon emitterを設定する。一方、その初期化では**シーン内ContactをDynamicsUsage.Worldに設定する**。ClientSim配下のC#を調査した範囲に標準Avatar Head Senderを生成・playerへ結び付ける経路は見つからなかった。ClientSimの人型表示やHead bone対応を、実アバターの標準Contacts対応と同一視しない。[公式ClientSim文書](https://creators.vrchat.com/worlds/clientsim/)も全機能を再現するものではないとしている。

| 検査 | ClientSimでの扱い |
|---|---|
| 領域計算、依存先無効化、UI出力、候補false | 通常構成で確認可能。今回のPhase 3としては未実施 |
| World Sender → ReceiverのEnter/Exit配送 | SDKにEditor経路あり。検証シーンのコピーでReceiverのContent TypesにWorldを一時追加し、HeadタグのWorld Sender（Content TypesにWorld）を重ねる。rawイベント数が増えても`validContactCount=0`と候補falseが正しい |
| ローカルの標準Head Sender、他人のHead、実アバター変更 | ClientSimの通常プレイヤーでは受け入れ試験の代替にしない。VRChat本体で確認 |
| 複数のローカルAvatar Senderの集合処理 | 代替ランタイムで分岐検査済み。複数World SenderのClientSim試験は配送／拒否試験に限り、受理された複数接触の実機試験とは区別 |
| Contactと領域がともにtrue | 実Avatar SenderのVRChat試験が必要。公開boolをInspectorで書き換える方法はSDK検証に数えない |

World SenderをClientSimプレイヤーの頭の子に置くだけではAvatar Senderにはならない。本番コードにWorld受理・ローカル偽装・デバッグ用boolバイパスは追加していない。World配送試験後はReceiverのContent TypesをAvatarのみに戻す。

## 6. 手動受け入れ試験（すべて未実施）

以下は期待結果であり実測結果ではない。UI TextとクライアントUdonログを記録する。接触中の値は少なくとも次のContact更新・PostLateUpdate後に読み、瞬間的な順序差と残留を区別する。Phase 2の境界誤差を混ぜないため、組み合わせ試験はまずD≈0と明確なD>1を使う。

| ID | 環境・操作 | 期待結果／観測点 |
|---|---|---|
| C1 | VRChat Build & Test。標準Head Contactが有効な既存アバターで、Receiverを頭へ近づけ、離す | ローカルHeadのEnterで数が増え、離脱で0・Contact=false。insideもtrueなら候補true、離脱後候補false |
| C2 | 同じローカルアバターの手・胴体・足にReceiverを接触。検証用コピーではReceiverにHand等のタグも追加してスクリプト側の拒否も確認 | Head一致がなければ数0、Contact=false、候補false。タグ追加時はrawイベント数だけ増えてよい |
| C3 | 他プレイヤーのHeadのみ接触、自分のHeadは離す | raw Enterは届き得るが数0、Contact=false、候補false。他人の接触・離脱が自分の有効接触を消さないことも確認 |
| C4 | 複数Head Senderを持つ既存アバターが利用できる場合、A・Bを同時接触→Aだけ離脱→B離脱 | 2→1→0、Aだけ離脱ではContact=trueを維持。同じSenderの再通知で数が増えない。適した既存アバターがなければ実機項目は未検証のまま残す |
| C5 | ローカルHead接触中にアバター変更。他人の変更も別途試す | ローカル変更で0・候補false。新アバターで一度離して再接触すると再登録。他人の変更はローカル接触を消さない |
| C6 | Head接触中にReceiver componentを無効化、別途GameObjectを無効化して戻す | 検出器の次の評価／OnDisableで0・候補false。再有効化だけで古い接触が復活せず、離脱・再接触で復帰 |
| C7 | 検出器のUdon Behaviourだけを無効化・再有効化 | 0・候補false。Receiverが接触継続している場合は一度離して再接触して復帰 |
| C8 | 接触中にSenderが無効化される既存アバターの機能が利用できる場合に操作。別途アバター切替で旧Sender破棄も試す | Exitで対象Senderだけ減算。破棄は無効Proxy掃除でも減算。数が残るならSDK配送限界として記録し、Phase 3を受け入れない。操作可能な既存アバターがなければSender単体無効化は未検証。アバター改変を必須にしない |
| C9 | Contact=false/true × inside=false/trueを4通り作る。頭とReceiverの接触位置を変えるか、検証用ProbeのBitePointとReceiverを独立配置 | 候補は両方trueのときだけtrue。Contactだけ／領域だけではfalse。有効な領域外はhasRegionResult=true |
| C10 | 接触中にRegion／Estimatorを無効化、BitePoint参照を外す | 領域無効・候補false。Contactの観察値は独立。参照復帰後は現在の条件で再評価 |
| C11 | 頭を素早く動かす。検出器・領域判定器・推定器の再有効化順を変える | 前フレームの領域を候補へ流用しない。Proxy再登録が必要な場合は再接触してから観察 |
| C12 | 接触中にAction Menuからスケール変更 | 現在眼高の口位置・領域へ更新。ContactはSDKの接触更新に従う。古い候補を保存しない |
| C13 | ClientSimの検証用コピーでWorld Head Senderを入出させ、Sender／Receiverを無効化 | rawイベント数とSDK配送を観察。ただし受理数は常に0。これをC1/C4/C8の合格扱いにしない |

C3は実際の別クライアント／別プレイヤーで検証する。通常の1クライアントBuild & Testだけでは満たせない。今回、公開アップロードは行っていない。

短時間のReceiver無効→有効が両方ともポーリング間に収まり、SDKからのExitも欠落した場合は検出できない。外部スクリプトでそのような切り替えを行う場合は切り替え時に`ResetContacts()`も呼び、再接触する。Receiverタグ・Content Typesの実行中変更も本番構成では行わず、変更試験後にはクリアして再接触する。

## 7. 実施した検査と結果

| 検査 | 結果 |
|---|---|
| SDK API、Proxy identity／isValidのIL、Udon extern公開 | 実施済み。上記のAPI制約を確認 |
| 本体3本を実SDK DLL参照でC#コンパイル | **成功、終了コード0** |
| 本体3本を代替Unity/VRC型で実行 | **11グループ／89アサーション成功、終了コード0** |
| Phase 2の既存代替ランタイム検査を変更後のコードで再実行 | **15グループ／134アサーション成功、終了コード0**。中心・境界・回転・スケール・無効化などの回帰を確認 |
| Git差分・空白検査 | 推定器は差分なし。領域判定器は更新入口とフレーム番号の9行追加のみ。`git diff --check`と新規ファイルの`git diff --no-index --check`で空白エラーなし |
| Unityバッチ起動 | **失敗**。`No valid Unity Editor license found. Please activate your license.`、ログ上return code 1。プロジェクトのインポート・executeMethod到達前に終了 |
| Unity/UdonSharpコンパイル・U#プログラムアセット生成 | **未実施**（上記起動失敗のため）。一時EditorスクリプトはAssetsから除去済み |
| Phase 3のClientSim実行 | **未実施** |
| Phase 3のVRChat実機・複数プレイヤー | **未実施** |

代替ランタイムではローカルHeadのEnter/Exit、対象外タグ・大小文字・他人・World・null・別Receiverの拒否、異なるProxyの同一Sender比較、重複Enter/Exit、複数Sender、40接触への配列拡張、破棄Sender・無効playerの掃除、Receiverと検出器の無効化／再有効化、参照差し替え、アバター変更、ANDの4通り、依存先欠損、両実行順序、同一フレームのBitePoint再評価、スケール更新、個別UI出力を検査した。

この検査は**本体の集合処理・演算・分岐**の検査であり、SDKの実際の接触検出やSender無効化時のExit生成、Unityイベント配送、Udon VM実行を検証しない。Senderの存在確認とenabledの違いを代替型で消して合格扱いにはしていない。

```text
PASS: 11 groups, 89 assertions (managed substitutes; NOT Unity/Udon runtime).
```

この環境での再実行コマンド（`.utmp/phase3`はローカル一時成果物）：

```powershell
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/Tools/netcorerun/netcorerun.exe' `
  'I:/Unity/Editor/2022.3.22f1/Editor/Data/DotNetSdkRoslyn/csc.dll' `
  -noconfig '@.utmp/phase3/Phase3.rsp'
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/Tools/netcorerun/netcorerun.exe' `
  'I:/Unity/Editor/2022.3.22f1/Editor/Data/DotNetSdkRoslyn/csc.dll' `
  -noconfig '@.utmp/phase3/Checks.rsp'
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/MonoBleedingEdge/bin/mono.exe' `
  '.utmp/phase3/Phase3Checks.exe'
```

Unity起動失敗ログは`.utmp/phase3/unity-compile.log`、分岐検査結果は`.utmp/phase3/check-results.txt`、Phase 2回帰結果は`.utmp/phase3/phase2-regression-results.txt`。レビュー用差分は`.utmp/phase3/phase3-review.diff`。ライセンス設定の変更やSDK／Unityアップグレードは行っていない。

## 8. 残る受け入れ条件

- UnityでU#プログラムを作成し、UdonSharpコンパイルとUdon VMでの実行を確認する。
- C1〜C13を実施し、実施できない複数Sender・Sender単体無効化は、代替ランタイムの結果と実機未検証を分けて記録する。
- 特にSender無効化時のExit、アバター変更時の通知順、Receiver再有効化後の新規Enterを確認する。欠落があれば2節の追加案を検討し、対応済みと誤記しない。
- 標準Head Contactが無効・利用できないアバターでは候補falseになる。アバター改変を要求せず、Contact単独または領域単独へのフォールバックもしない。
- Phase 4以降で相対速度・前方条件・自由状態・再取得禁止を追加する。候補がfalseになったことを、将来の口保持解除条件にしない。

| 日付・環境 | ID | 実測接触数／Contact／inside／候補 | Console・Udonログ | 判定 |
|---|---|---|---|---|
| 未実施／Unity・ClientSim・VRChat | C1〜C13 | — | — | 受け入れ保留 |
