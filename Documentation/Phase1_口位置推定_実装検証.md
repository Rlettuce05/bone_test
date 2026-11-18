# Phase 1：口位置推定の実装・互換性調査・検証記録

調査日：2026-11-13。要求の正本は `VRC_骨の口保持ギミック_設計書.md`。
検証結果の反映日：2026-11-18（ユーザー報告に基づく。CodexによるUnity再検証は未実施）。
対象はPhase 1の口位置推定とローカル可視化のみ。UnityへのUdonSharpスクリプト登録と、ClientSimでの頭部移動・回転へのデバッグマーカー追従は確認済み。追加のユーザー報告により、VRChat本体でも手持ちのアバターでbeta=0なら概ね口位置に追従することを確認済み。**Phase 1は暫定完了とし、パラメータの最適化は保留する。** 定量的な推定精度評価や未報告の個別試験まで完了したことは意味しない。

Phase 2では推定式を維持し、同一フレームの推定結果を共有する`RefreshMouthPose()`と`mouthPoseFrame`を追加した。変更範囲・実行順序・検証状況は[Phase 2の記録](Phase2_楕円体領域判定_実装検証.md)を参照。以下の初回実装記録は、その時点の構成を示す。

## 1. プロジェクト構成と変更範囲

以下の構成・追加ファイル・変更範囲は、初回調査・実装時点（2026-11-13）の記録。

| 項目 | 確認結果 |
|---|---|
| Unity | `ProjectSettings/ProjectVersion.txt`：2022.3.22f1 |
| VRChat SDK | `Packages/vpm-manifest.json`と導入済み各`package.json`：Base / Worldsともに3.10.6 |
| UdonSharp | `Packages/com.vrchat.worlds/Integrations/UdonSharp`に同梱。独立パッケージのバージョン番号は確認できないため、Worlds 3.10.6同梱版として識別 |
| ClientSim | `Packages/com.vrchat.worlds/Integrations/ClientSim`に同梱 |
| シーン | `Assets/Scenes/VRCDefaultWorldScene.unity`。保存済みAssetsに骨モデル、BitePoint、独自Pickup構成は確認できず |
| 既存スクリプト | `Assets/UdonSharp/UtilityScripts`にTrackingDataFollower、BoneFollowerなど |
| 試作 | `Reference/Prototype/MouthPickupController.cs`、`MouthContactDetector.cs`。Assets外なので通常のUnityインポート対象外 |
| Git開始点 | `33fc4a2`。開始時の作業ツリーはクリーン |

追加ファイル：

- `Assets/BoneMouthPickup/MouthPositionEstimator.cs`
- `Documentation/Phase1_口位置推定_実装検証.md`（本書）

既存ファイル、設計書、試作は編集しない。Scene、Prefab、Packages、ProjectSettings、既存`.meta`にも変更を加えない。コミット・pushは行わない。

初回調査時はU#プログラムアセットと検証用オブジェクトが未作成だった。その後、ユーザーがUnityへのスクリプト登録とClientSimでの追従確認を実施した（詳細は第5・6節）。下記の設定手順は再現用として残す。インポート・コンパイル時にはUnityが新規`.meta`、UdonSharpプログラム`.asset`、`Assets/SerializedUdonPrograms`内のコンパイル済みアセットを生成する。これはU#の実行に必要な生成物であり、手書きでGUIDやScene YAMLを作成・置換しない。検証シーンを保存する際も既存シーンとは別名にする。

前回のClientSim検証結果反映では本書のみを編集した。今回のPhase 2開始時の変更は、上記のPhase 2記録へ分けて記載する。

## 2. 試作の互換性レビュー

### 先に解消すべき問題（後続Phase）

1. **Manual同期とVRC Object Syncの同居**
   - 試作は`BehaviourSyncMode.Manual`を指定し、同じBoneに`VRCObjectSync`を置く。
   - 導入済み`Integrations/UdonSharp/Editor/Editors/UdonSharpGUI.cs`には、この組み合わせを不可とするエラー表示がある。`UdonSharpEditorManager.cs`にも警告処理がある。
   - 最小の修正案は、同じオブジェクトに置く保持状態・保持者IDの同期をContinuousに合わせること。その場合はManual用の`RequestSerialization()`に依存しない送信へ整理する。Manualを維持する別オブジェクト案は、所有者の整合管理が追加で必要になる。
   - 設計書は`UdonSynced`の方式まで固定していないが、採用案と送信挙動はPhase 7着手前に明示する。今回のPhase 1では同期処理を導入・変更しない。
2. **Contactの保持方法**
   - 単一boolでは複数のローカルHead senderが重なった場合、一つのExitで接触全体をfalseにしてしまう。
   - Exit時にsenderが無効なら早期returnするため、trueが残る可能性がある。
   - ReleaseやPickup時にboolをfalseにした後、接触し続けていると新しいEnterが来るまで復帰しない。
   - Phase 3で有効接触の識別・集計・破棄を検討する。接触だけを取得条件にする変更はしない。
3. **所有権移動・退出・途中参加**
   - `SetOwner`直後の所有者確認と同期済み状態の読み取りだけでは、競合時の通知順序までは保証できない。
   - `OnOwnershipTransferred`内で`ReleaseMouth()`が走る場合も、その後の取得処理でクールダウン等を再確認していない。
   - `OnPlayerLeft`はなく、退出復旧を所有権移動に依存している。保持者退出、所有権移動、遅れて届く状態の整合と途中参加をPhase 7で検証する。

### APIと数式

- 試作2本は、導入済みSDKの実アセンブリを参照したRoslyn C#コンパイルを通過した。使用型・メンバー・overrideシグネチャにC#としてのエラーは検出されなかった。ただしUdonSharp変換・シーン設定・VRChat動作の保証ではない。
- `OnContactEnter(ContactEnterInfo)` / `OnContactExit(ContactExitInfo)`は、この版の`UdonSharpBehaviour.cs`に存在する。引数の名前空間は`VRC.Dynamics`。sender proxyの`isValid`と`player`もC#参照が解決した。
- `Runtime/Udon/Dynamics/ContactReceiverUdonEmitter.cs`はReceiverと同じGameObjectのUdonBehaviourへイベントを送る。[公式Contacts仕様](https://creators.vrchat.com/common-components/contacts/)とも一致する。Contact APIを存在しない旧APIと判断する理由はない。
- 試作の口推定式は設計式(1a)(1b)に一致する。相対速度の符号・分解も式(4)〜(6)に一致し、正の最小速度閾値は導入されていない。
- 取得条件と保持追従は分離されており、MOUTH中の一時的なContact喪失だけでは解除しない。
- 試作のBitePoint姿勢式は、**Boneと祖先のスケールが(1,1,1)、BitePointがBoneの直下**という前提付き。Phase 1はBone/BitePointを参照せず、この前提を必要としない。
- アバター変更時の推定履歴初期化はあるが、試作ではFREE中の変更に対するクールダウン追加はない。後続の取得実装で再確認する。

## 3. Phase 1のクラス・使用API

`MouthPositionEstimator : UdonSharpBehaviour`がM1を担当する。

| API / イベント | 用途・根拠 |
|---|---|
| `Networking.LocalPlayer` / `Utilities.IsValid` / `VRCPlayerApi.isLocal` | ローカルプレイヤーの取得・有効性確認。初期化が遅れても毎フレーム再取得 |
| `GetTrackingData(VRCPlayerApi.TrackingDataType.Head)` | ワールド座標の頭部位置・回転 |
| `GetAvatarEyeHeightAsMeters()` | 現在の設定眼高H。しゃがんだ際の床からの距離ではない |
| `GetBonePosition(HumanBodyBones.Head)` | Head Bone位置。betaが0なら呼び出さない |
| `PostLateUpdate()` | 頭部更新後の推定とマーカー更新。導入済みUdonSharpのイベント定義に存在 |
| `OnAvatarChanged(VRCPlayerApi)` | ローカルアバター変更時に古い出力を無効化 |
| `OnAvatarEyeHeightChanged(VRCPlayerApi, float)` | ローカル眼高変更時に古い出力を無効化。引数の以前の眼高はスケール計算に使わない |
| `Quaternion.Inverse`、Quaternion / Vector3演算 | 頭部ローカル座標への変換と推定式 |
| `Transform.SetPositionAndRotation` / `IsChildOf`、`GameObject.SetActive` | 独立したデバッグマーカーの配置・表示と誤参照防止 |

位置取得は[Player Positions](https://creators.vrchat.com/worlds/udon/players/player-positions/)、眼高は[Player Avatar Scaling](https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/)、変更通知は[Avatar Events](https://creators.vrchat.com/worlds/udon/avatar-events/)を参照。導入済みClientSimにも、上記の位置・眼高取得とアバター／眼高イベント送信の実装がある。

実装する式：

```text
s = H / H0
mouthOffset = (1 + alpha * (s - 1)) * o0
            + beta * (inverse(RH) * (pB - pH) - s * d0)
mouthPosition = pH + RH * mouthOffset
mouthRotation = RH
```

設計との対応・採用した動作：

- alpha=beta=0で固定オフセット、alpha=1・beta=0で眼高比例、beta≠0で任意のHead Bone補正。
- Head Boneが`Vector3.zero`なら、そのフレームのbeta項全体を省略する。基準骨位置の項だけを残さない。
- HまたはH0が非正なら、暫定的にs=1とする。これは入力不備時のフォールバックで、通常の正の眼高比は丸めたり制限したりしない。
- 全入力を毎`PostLateUpdate`で読み直す。アバター／眼高変更時は出力をリセットし、次の`PostLateUpdate`で再計算する。過去のアバターの値をキャッシュしない。
- 非ローカルの変更イベントは無視する。ローカルプレイヤー未取得・推定器無効化時にはマーカーを非表示にする。
- `BehaviourSyncMode.None`。取得、Contact、楕円体、速度判定、口保持、Pickupイベント、所有権操作、ネットワーク同期は含まない。
- 推定器自身のTransformは更新しない。マーカーに推定器自身またはその祖先が指定された場合は、移動・表示切替を行わない。

出力は`hasMouthPose`、`mouthPosition`、`mouthRotation`、`avatarScale`、`headBoneCorrectionApplied`。すべてローカルの実行時値。将来参照する場合は`hasMouthPose`を先に確認する。`hasMouthPose`は計算を行えたことを示し、口の実位置との一致やトラッキング品質の保証ではない。

## 4. Unityでの設定手順

ユーザー報告によりスクリプトのUnity登録とClientSimでの動作を確認済み。以下は再現用手順であり、全手順を記載どおりに実施したことや、Console・コンパイルログを確認したことまでは今回の報告に含まれない。

1. VCCからこのプロジェクトをUnity 2022.3.22f1で開く。`Reference/Prototype`のコードはAssetsへコピーしない。
2. 新規スクリプトのインポートを待ち、ConsoleのC#エラーを確認する。
3. Projectで`Assets/BoneMouthPickup`を選び、`Create > VRChat > Udon > Udon C# Program Asset`から`MouthPositionEstimator.asset`を作成する。このメニューは導入済み`UdonSharpProgramAsset.cs`の`CreateAssetMenu`定義で確認済み。
4. アセットの`Source Script`へ既存の`MouthPositionEstimator.cs`を割り当てる。すでに同じソースに対応するプログラムアセットが生成されている場合はそれを使い、重複作成しない。新しい同名C#を作る`Create Script`は使わない。
5. プログラムInspectorのコンパイル操作を実行し、ConsoleでUdonSharpエラーがないことと、コンパイル済みプログラムが生成されたことを確認する。
6. 標準ワールドシーンをUnityの`Save As`で`Assets/Scenes/MouthPositionPhase1Test.unity`などの別名に保存する。既存の骨が別シーンにある場合も、検証用コピーで作業する。
7. ルートに空オブジェクト`MouthEstimateDebug`を作る。`Udon Behaviour`を追加し、`Program Source`に上記アセットを指定する。推定器とマーカーにVRC Object SyncやPickupを追加しない。
8. 別のルートオブジェクトとしてSphere `DebugMouthMarker`を作り、Scaleを`(0.02, 0.02, 0.02)`にする。Sphere Colliderを削除し、Rigidbodyは追加しない。骨や動くオブジェクトの子にしない。
9. `Debug Mouth Marker`にSphereのTransformを指定し、`Show Debug Marker`をONにする。回転も見たい場合はマーカーに細長いCubeを子として付け、ローカル+Z方向へ置く。CubeのColliderも削除する。
10. 推定器の係数を設定する。設定後、Consoleにエラーがないことを再確認して検証シーンを保存する。

| Inspector項目 | 初期値 | 意味 |
|---|---|---|
| Base Mouth Offset | (0, -0.10, 0.08) | o0、頭部ローカルのm。+Zが前 |
| Reference Head Bone Offset | (0, -0.08, -0.04) | d0、基準眼高における頭部ローカルのm |
| Reference Eye Height | 1.6 | H0、正のm |
| Alpha | 1 | スケール補正係数 |
| Beta | 0 | 骨格補正係数。まず0で比較する |

数値は試作由来の**未調整の試験開始値**。制作者が共通値を調整するものであり、来訪者ごとの計測やキャリブレーションUIは作らない。

## 5. 手動テストと期待結果（一部実施済み）

### 今回の検証結果（ユーザー報告）

| 項目 | 状態・結果 |
|---|---|
| UdonSharpスクリプトのUnity登録 | 実施済み |
| ClientSimでのデバッグマーカー追従 | 頭部の移動・回転に追従することを確認済み |
| VRChat本体での口位置追従 | beta=0で手持ちアバターの口位置へ概ね追従することを確認済み。定量的精度評価は未実施 |
| Phase 1の受け入れ | 暫定完了。パラメータ最適化は保留 |

確認済みの範囲はUnity登録、ClientSimの基本追従、VRChat本体の手持ちアバターでの概ねの口位置追従。実機ではbeta=0との報告がある。その他の係数、操作ごとの詳細、定量的な追従誤差、Console・コンパイルログの結果は未報告。ユーザーの判断によりPhase 1を暫定完了とする。

### ClientSim：Unity内の機能確認

下記手順1〜2に関連するPlay実行と頭部移動・回転への追従は確認済み。しゃがみ単独の挙動、回転確認用の子オブジェクトの使用、および手順3〜9の追加検証は実施報告がなく、確認済みに含めない。

1. ClientSimを有効にして検証シーンをPlayする。SceneビューとGameビューを並べる。マーカーは顔に近く、Gameの一人称カメラではクリップされる場合があるため、Sceneビューで確認する。
2. キーボード移動、マウス視点移動、しゃがみを行う。マーカーが頭部の移動・回転に追従すること。回転確認用の子を付けた場合は頭部前方と揃うこと。
3. `alpha=0, beta=0`で実行する。ClientSimのプレイヤー身長設定を変更しても、頭からマーカーへの頭部ローカルオフセットが`(0,-0.10,0.08)`のままであること。
4. `alpha=1, beta=0, H0=1.6`で眼高Hを0.8、1.6、3.2mに変える。期待する頭部ローカルオフセットは順に`(0,-0.05,0.04)`、`(0,-0.10,0.08)`、`(0,-0.20,0.16)`。ClientSimの身長設定を起動前に変更する場合は、各条件でPlayを開始し直す。
5. しゃがみだけでは設定眼高に基づくsが変化しないことを確認する。
6. `beta=0`と`beta=0.5`で比較する。Head Boneが取れる場合、差分が`0.5 * (inverse(RH)*(pB-pH) - s*d0)`に一致すること。ClientSimのアバター骨格は実アバターと異なるため、ここでは計算・更新の確認に限定する。
7. `Show Debug Marker`をOFF/ONにし、非表示／再表示を確認する。マーカー未指定でも例外がないことを確認する。推定器のGameObjectを無効化するとマーカーが消え、再有効化すると推定が再開すること。
8. マーカー欄に推定器自身、次に推定器の親を誤指定する。対象の移動・無効化が起きないこと。確認後は正しいマーカーへ戻す。
9. Playを終了し、係数の試験変更を保存する場合はEditモードで共通設定を入れ直す。Consoleのエラー件数と結果を記録する。

### VRChat：実アバター・変更イベント・誤差評価

VRChat本体でbeta=0の基本追従は確認済み。以下は詳細な再現・追加評価手順として残す。個々の操作、変更イベント、誤差の数値測定については実施報告がなく、確認済みとはしない。

1. UnityのVRChat SDK Control Panelで検証シーンを`Build & Test`する。公開アップロードは不要。まずDesktop、利用可能ならVRでも試験する。
2. アバターを改変せず、利用可能な複数のアバターで確認する。鏡またはVRChatカメラでマーカーと実際の口を観察する。鏡はマーカーのレイヤーが反射対象であることを確認する。
3. 同じ係数で、通常体型、小型、大型、顔形状の異なるアバターを比較する。頭の左右回転・上下回転、移動、しゃがみを行い、安静時と動作中のずれを記録する。
4. その場でアバターを切り替える。変更後のHead Tracking・眼高・Head Boneでマーカーが再配置され、以前のアバターの補正値が残らないこと。
5. スケール変更が許可された環境で、Action Menuのアバタースケールを変える。現在の眼高に追従し、元に戻すと同じ姿勢・同じ係数では同じ推定結果へ戻ること。保存された眼高を持つアバターへの切替も確認する。
6. Head Boneを取得できないアバターを利用できる場合、betaを非ゼロにしても原点へ引かれず、スケール補正だけになること。利用できない場合はこの項目を「未実施」と記録し、アバター改変を要求しない。
7. 複数人で確認する場合、他人のアバター変更で自分のマーカーがリセットされないことを確認する。マーカー位置は各クライアント自身の口位置であり、他人に同じ座標を送信する仕様ではない。
8. 既存の骨を含む検証シーンが用意できた場合、手Pickup／Dropを確認する。今回は保存済みAssetsに対象の骨がないため、この回帰試験は未実施。

記録表（数値測定できないものは目視結果として区別する）：

| アバター／Desktop・VR | 眼高H | alpha / beta | 姿勢 | 口とのずれ[m]または目視結果 | Console / clientエラー |
|---|---|---|---|---|---|
| 手持ちアバター（動作モード詳細未報告） | 未報告 | alpha未報告 / beta=0 | 詳細未報告 | 概ね口位置に追従（ユーザーの目視報告） | 未報告 |

評価に用いる実際の口位置は測定・比較専用とし、ギミックの実行時入力へ取り込まない。

## 6. 実行済みチェックと残る制約

2026-11-13の調査結果と2026-11-18のユーザー報告を区別して記録する。実機での基本追従以外の追加項目は、以下のとおり未確認・未実施のままとする。

| チェック | 結果 |
|---|---|
| 固定SDKのソース・実アセンブリと公式API仕様の照合 | 実施済み |
| 試作2本のRoslyn C#コンパイル | 成功、終了コード0 |
| Phase 1スクリプトのRoslyn C#コンパイル | 成功、終了コード0 |
| 差分・空白検査（初回実装時） | `git diff --check`および新規2ファイルの`git diff --no-index --check`で空白エラーなし。当時の既存追跡済みファイルに差分なし |
| UdonSharpスクリプトのUnity登録 | 実施済み（ユーザー報告） |
| Unity Editorのインポート・C#コンパイル結果のログ確認 | 未確認。Unity登録・ClientSim動作の報告はあるが、ログやエラー件数は未報告 |
| U#プログラムアセットの作成手順・UdonSharpコンパイル結果のログ確認 | 未確認。登録・実行済みの報告と、個別の作成手順・コンパイルログの確認は区別する |
| ClientSim Playテスト：頭部移動・回転へのマーカー追従 | 確認済み（ユーザー報告）。実アバターでの口位置推定精度は評価していない |
| ClientSim追加テスト：alpha / beta・眼高変更、表示切替、誤参照など | 未実施（追加の実施報告なし） |
| VRChat本体での実アバター基本追従 | beta=0で概ね口位置に追従することを確認済み（ユーザー報告）。Build & Testの操作・ログ詳細は未確認 |
| 実アバターでの口位置推定精度 | 上記の目視結果あり。定量評価とパラメータ最適化は保留 |
| 既存骨のPickup回帰試験 | 対象アセットを確認できず、未実施 |

C#検査では、Unity付属の`Data/Tools/netcorerun/netcorerun.exe`から`Data/DotNetSdkRoslyn/csc.dll`を実行した。生成済み`Assembly-CSharp.csproj`の参照先と、対応する`Library/ScriptAssemblies`のアセンブリを**読み取り専用で参照**し、検査成果物だけをGit除外済み`.utmp/phase1`へ出力した。SDKやLibraryのファイルは編集していない。

```powershell
& 'I:/Unity/Editor/2022.3.22f1/Editor/Data/Tools/netcorerun/netcorerun.exe' `
  'I:/Unity/Editor/2022.3.22f1/Editor/Data/DotNetSdkRoslyn/csc.dll' `
  -noconfig '@.utmp/phase1/Phase1.rsp'
# 試作の検査は最後の引数を @.utmp/phase1/Prototype.rsp に変更する。
```

応答ファイルはこの環境の一時検査用であり、配布物ではない。他の環境では固定Unityと導入済みSDKの参照先に合わせて再生成する。C#検査はUdon VMのextern公開、UdonSharpコード変換、イベント順序、実行時動作を検証しない。

初回調査時はUnity実行ファイルが存在し、`Library/EditorInstance.json`のPIDに対応するUnityプロセスも起動中だった。当時のセッションにはUnity UI操作用ランタイムが提供されていなかったため、CodexによるUnity側の検証は未実施だった。その後のユーザー報告により、Unity登録とClientSimでの基本追従を確認済みへ更新した。今回の文書更新でもCodexによるUnity操作・コンパイル・Playテストの再実行は行っていない。

既知の制約・未決定事項：

- 初期係数の精度は未評価。Head Tracking・Head Bone・眼高だけでは、すべての顔形状の実際の口位置を正確に復元できるとは限らない。
- `GetBonePosition`の欠損値はゼロ座標なので、実在するHead Boneがワールド原点にある場合とも区別できない。UnityのVector3比較の許容誤差を含め、原点付近では補正を省略する場合がある。
- 眼高変更時は新しい式の値へ直接更新する。Phase 1では推定式を観察するため位置平滑化を加えない。将来の速度履歴リセット・保持解除・再取得禁止は各保持／取得Phaseで実装する。
- マーカーは専用の独立オブジェクトを指定する。任意の別オブジェクトを誤指定した場合まで、それが骨や同期対象であることを判定する仕組みはない。
- ClientSimでの追従確認だけではアバター間の推定精度やVRの実トラッキングを評価できない。
- ClientSimおよびVRChat本体での基本追従を受けて、Phase 1は暫定完了。未実施・未確認項目と係数の最適化は保留し、Phase 2へ進む。
