# VRChatアバターサムネイル撮影管理ツール (Avatar Thumbnail Manager) v2.0

シーン内のVRChatアバターのサムネイル(1024×1024 PNG)を一括撮影・管理するUnityエディタ拡張です。

メニュー: `ろりぽっぷCANDY > あばたーはぶリスト作成ツール`

## v2.0の主な変更
- 内部構造を刷新し軽量化(元シーンを一切変更せず、プレビュー専用シーンに複製して撮影)
- アバター名を変更しても別アバターとして再登録されない(GlobalObjectId / blueprintId で同一判定)
- 背景色をアバターの配色から自動設定(類似色/補色)
- NDMF処理後の状態で撮影するため、MA Bone Proxy・着せ替え/髪の切り替えが写り込まない
- 撮影画像をVRC SDKのアバターアイコンに設定するボタン(自動設定を試行、失敗時は手動案内)
- UI改善(カメラ操作のUndo/Redo、4:3クロップガイド、ステータス表示)

## 出力
`Assets/AvatarThumbnailManager/log/[シーン名]/[アバター名].png` と `AvatarDatabase.json`

## 動作環境
Unity 2022.3 / VRChat SDK(Avatars)。NDMF・Modular Avatarは任意(リフレクション連携のため未導入でも動作)。

## インストール
VCCの「Add Repository」に VPM リポジトリURLを追加し、本パッケージをプロジェクトに追加してください。

## 注意
- 非表示(Hierarchy上で無効)のアバターも複製して有効化し撮影できます。
- 既存の撮影済み画像は、アバター名変更後も旧ファイル名のまま維持されます。
- SDKアイコンの自動設定はVRChat SDKの内部APIに依存するためベストエフォートです。

## ライセンス
MIT
