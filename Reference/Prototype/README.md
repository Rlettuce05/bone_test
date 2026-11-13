# VRChat「骨を口でくわえる」UdonSharp試作実装

2026-11-09 時点の VRChat Creator Docs に沿った、設計書の M1〜M5 を接続する**試作コード**です。

**このパッケージはUnity/VRChatクライアントでのコンパイル・実機テストを行っていません。** まず1人で動作を検証し、その後2クライアント以上で所有権競合と途中参加をテストしてください。

## 事前準備

- VCCで **Worlds** プロジェクトを作り、対象のワールドを開く。SDK 3.10.6 のリリース情報では Unity 2022.3 使用が指定されています（Unity 6へ更新しない）。
- Unityで `Assets > Create > U# Script` から同名スクリプトをそれぞれ作り、生成された `.cs` の内容を配布コードに置き換える（U#プログラムアセットも生成する確実な手順）。既存 `.cs` を直接取り込む場合は、対応するUdonSharpプログラムアセットが存在することを確認する。コンパイルエラーがないことを確認。
- 骨のゲームオブジェクトに **Rigidbody / Collider / VRC Pickup / VRC Object Sync / MouthPickupController** を追加する。Rigidbody、Collider、VRC Pickupは**同じGameObject**に配置する。既存の手Pickupを流用してよい。
- 骨ルートおよびその祖先のスケールを `(1,1,1)` とする。骨モデルのサイズはメッシュ子オブジェクト側で調整し、骨のルートには親のスケーリングがかからないようにする。
- 初期物理動作はRigidbodyの `Is Kinematic = OFF`, `Use Gravity = ON`（試作の推奨初期値）。VRC Object Syncを使う場合、ランタイム切り替えは `SetKinematic` / `SetGravity` を通す。

## Hierarchy

```
Bone  (Rigidbody, Collider, VRC Pickup, VRC Object Sync, MouthPickupController)
├── Mesh  (表示用。モデルのスケーリングはこちら側で調整)
└── BitePoint  (空のTransform。骨を噛む位置・向き。Boneの直下)
    └── HeadContactReceiver  (VRC Contact Receiver, MouthContactDetector)

DebugMouthMarker (任意。球体のColliderを削除)
DebugEllipsoid (任意。半透明の球体のColliderを削除)
```

1. `BitePoint` を骨のくわえ位置に置く。直接の子でなければ現行コードの位置変換式は成立しない。
2. `HeadContactReceiver` を BitePoint の原点付近に配置。Sphere型、半径 **0.12〜0.18m程度から試す**。数値は未検証の出発点であって確定値ではない。VRChat ContactはUnity Colliderとは別の仕組み。
3. `VRC Contact Receiver` の **Content Types に Avatar**、**Collision Tags に `Head`** を指定。デフォルトのアバターHead senderから接触を受ける。
4. 同じ `HeadContactReceiver` に `MouthContactDetector` を配置し、`controller` に Bone 側 `MouthPickupController` を割り当てる。
5. Bone 側 `MouthPickupController` の `bitePoint`, `pickup`, `objectSync`, `boneRigidbody` に対象コンポーネントを割り当てる。
6. `debugMouthMarker`, `debugEllipsoid` を割り当てれば、ローカルプレイヤーに対する推定口位置と取得楕円体を可視化できる。Debug用オブジェクトは**Boneの子にしない**。コライダーは外し、Transform同期コンポーネントも付けない。
7. プレイヤーの手で骨を持つ既存の動作が維持されていることを確認する。

## 初期パラメータ（すべて試験用）

| インスペクタ項目 | 初期値 | 意味 |
|---|---|---|
| Base Mouth Offset | (0, -0.10, 0.08) | Head Tracking原点から口への推定相対位置[m] |
| Reference Eye Height | 1.6 | アバターの基準眼高[m] |
| Alpha | 1.0 | スケール補正を適用 |
| Beta | 0.0 | Head Bone補正は計測してから有効化 |
| Base Radii | (0.08, 0.07, 0.12) | 楕円体半径[m] |
| Radius Scale Coefficients | (1,1,1) | 楕円体を眼高比で補正 |
| Max Approach Angle | 55度 | 相対速度の前方向と一致する角度 |
| Velocity Smoothing | 0.35 | EMA係数 |
| Approach Memory | 0.10秒 | 接近履歴の保持 |
| Drop Cooldown | 0.4秒 | 離した直後の誤再取得を防ぐ |
| Grip Euler Angles | (0,0,0) | Headの向きに対する骨の咥え向き |

## 動作と試験順

1. **位置推定**: DebugMouthMarker を表示し、数種類のアバターで口の位置と合うかを確認。補正係数 `beta` は0のまま開始する。次にDebugEllipsoidの大きさを評価する。
2. **Contact**: HeadContactReceiverにHeadが触れているとき `localHeadContact == true` になることを確認。口元で接触が成立しない場合、Receiverの半径や位置、Content Types、タグを確認。
3. **方向判定**: 骨を手に持たずに頭の正面から近づき、口に入ったときだけ取れるか確認。横切る動き、口から離れる動き、頭以外の接触で取得しないか検証。
4. **保持**: 口保持中は `BitePoint` が推定口位置に一致する。Use入力（デスクトップでは通常左クリック、VRではトリガー）で解除。これは暫定入力仕様で、他のUse操作と干渉する可能性がある。
5. **手への受け渡し**: 手で拾うと口追従が止まり、`VRC Pickup` に移行する。手から口への直接受け渡しは対象外。
6. **マルチプレイヤー**: SDKの `Build & Test` を2クライアント以上で実行。同期、同時取得、途中参加、退出、アバター変更、スケール変更を確認。

## 注意点・未実装

- **同期と競合**: `UdonSynced` で保持状態・IDを共有し、Object SyncでTransformを共有する**最小構成**。所有権取得が衝突するケース、通信遅延、同一フレームの状態遷移は完全には解決していない。実装を本番投入する前に競合試験が必要。
- **計算誤差**: `v_parallel > 0` は設計書をそのまま適用。ノイズに起因する誤検出がある場合、履歴・平滑化・領域の調整で対処する。固定の正の `v_min` は採用していない。
- **Contact**: 多くのHumanoidアバターにはデフォルトの `Head` Senderが存在するが、アバター作者による標準Colliderの変更やデータの遅延で接触の発生位置が変わり得る。
- **姿勢**: `BitePoint` がBone直下、Boneと祖先のスケールが1である前提。親側に縮尺があるPrefabなら階層を直すか、位置変換式をスケール対応にする。
- **眼高基準**: Head Trackingと顔の位置はアバター毎に異なる。`baseMouthOffset`などは測定用の初期値であり、どんなアバターでも一致する保証はない。
- **手と口の入力競合**: Use入力はワールドの他の仕組みと競合しうる。製品化時は解除操作のUXを見直す。
- **スケール変更**: 保持中は位置推定を随時更新するが、スケール急変に対する移動量制限・解除は未実装。速度履歴はリセットする。

## 公式参考資料

- [VRChat Contacts](https://creators.vrchat.com/common-components/contacts/)
- [Built-In Contact Tags](https://creators.vrchat.com/common-components/contacts/built-in-contact-tags/)
- [Player Positions](https://creators.vrchat.com/worlds/udon/players/player-positions/)
- [Player Avatar Scaling](https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/)
- [Event Execution Order](https://creators.vrchat.com/worlds/udon/event-execution-order/)
- [VRC Object Sync](https://creators.vrchat.com/worlds/components/vrc_objectsync/)
- [VRC Pickup](https://creators.vrchat.com/worlds/components/vrc_pickup/)
- [Input Events](https://creators.vrchat.com/worlds/udon/input-events/)
- [Networking / Ownership](https://creators.vrchat.com/worlds/udon/networking/ownership/)
