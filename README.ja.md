# Mc3E.Client

[English](README.md)

QnA互換MCプロトコル3EバイナリフレームをTCPで利用する、三菱MELSEC／
KEYENCE KV対応の外部依存なし.NETライブラリです。

> [!WARNING]
> MCプロトコル通信は暗号化も認証も行いません。信頼できる隔離制御ネットワーク、
> または適切に保護されたVPN内だけで使用してください。PLCポートをインターネットへ
> 直接公開しないでください。書込みは接続設備へ影響する可能性があります。

## 機能

- 三菱`D`ワード／`M`ビットの一括読出し・書込み
- KEYENCE `DM`ワード／`MR`ビットの一括読出し・書込み
- CPU型式、状態、一部診断情報、システム概要
- CPU型式取得`0101/0000`による読出し専用のメーカー／ポート判定
- インスタンスAPIとstatic API
- テスト用に差し替え可能な通信層
- `netstandard2.0`／`net8.0`対応
- 実行時NuGet依存パッケージなし

## インストール

パッケージIDとバージョンの公開後に実行します。

```console
dotnet add package Mc3E.Client --version 1.0.0
```

## 読出し・書込み

```csharp
using Mc3E;

var kv = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000);

ushort[] words = kv.ReadWords("DM", 60000, 11);
bool[] bits = kv.ReadBits("MR", 60000, 2);

kv.WriteWords("DM", 60002, new ushort[] { 12345 });
kv.WriteBits("MR", 60000, new[] { true, false });

var q = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002);
ushort[] d = q.ReadWords("D", 100, 3);
bool[] m = q.ReadBits("M", 100, 8);
```

ワードは16ビット符号なし整数です。1回の操作は、安全側の制限として256点までに
しています。これはMCプロトコルや各PLCの仕様上限ではありません。

KEYENCE `MR`の末尾2桁には`00`～`15`を指定します。`MR60015`の次は
`MR60100`で、`MR60016`は無効です。三菱`M`には連続ビット番号を指定します。

## CPU情報と自動判定

```csharp
var detected = Mc3EClient.DetectPlcAt("192.168.0.10");

var client = new Mc3EClient(
    "192.168.0.10", detected.Vendor, detected.Port);

CpuModelInfo model = client.ReadModel();
CpuStatusInfo status = client.ReadStatus();
CpuDiagnostics diagnostics = client.ReadDiagnostics();
CpuSystemSummary summary = client.ReadSystemSummary();
CpuInformation all = client.ReadCpuInformation();
```

ポート省略時は`5000`、`5002`、`1025`、`1026`、`4999`、`5010`を試します。
これは一般的な案件と実機試験の設定に基づく便宜的な候補で、MCプロトコル仕様が
割り当てたポート一覧ではありません。

KEYENCEのCR／CMには、MCの特殊リレー（`SM`）／特殊レジスタ（`SD`）コードで
アクセスします。三菱の診断・状態取得はQシリーズ互換のSM／SD領域を使用します。
対象CPUのマニュアルで特殊デバイスの意味を確認してください。

## static API

```csharp
ushort[] words = Mc3EClient.ReadWordsAt(
    "192.168.0.10", PlcVendor.Keyence, 5000, "DM", 60000, 11);

Mc3EClient.WriteWordsAt(
    "192.168.0.10", PlcVendor.Keyence, 5000, "DM", 60002,
    new ushort[] { 12345 });

bool[] bits = Mc3EClient.ReadBitsAt(
    "192.168.0.20", PlcVendor.Mitsubishi, 5002, "M", 100, 8);

CpuInformation cpu = Mc3EClient.ReadCpuInformationAt(
    "192.168.0.20", PlcVendor.Mitsubishi, 5002);
```

KEYENCE CR／CMと三菱SM／SDの読出しにもstatic methodを用意しています。

## 動作確認用コンソールアプリ

```console
dotnet run --project samples/Mc3E.Console -- status --ip 192.168.0.10
dotnet run --project samples/Mc3E.Console -- read DM 60000 11 --vendor keyence --ip 192.168.0.10 --port 5000
dotnet run --project samples/Mc3E.Console -- readbit MR 60000 2 --vendor keyence --ip 192.168.0.10 --port 5000
```

`read`、`write`、`readbit`、`writebit`、`model`、`status`、`errors`、
`summary`、`info`に対応します。実行内容はカレントディレクトリの
`output/yyyyMMdd_log.txt`へ英語で追記します。

## ビルド・テスト・pack

```console
dotnet restore Mc3E.sln
dotnet test Mc3E.sln -c Release
dotnet pack src/Mc3E/Mc3E.csproj -c Release -o artifacts
```

生成物は`artifacts/Mc3E.Client.1.0.0.nupkg`です。

## 対応範囲

- TCP上のQnA互換3Eバイナリフレーム
- 経路はネットワーク`0`、PC番号`FF`、I/O番号`03FF`、局番`0`
- 通常デバイスは三菱`D`／`M`、KEYENCE `DM`／`MR`
- KEYENCE CR／CMと三菱SM／SDの一部読出し
- 3Eバイナリ応答サブヘッダ`D0 00`を厳密に検証

ASCII、UDP、1E／4Eフレーム、他局中継、その他の通常デバイスは未対応です。

## 実機確認

- KEYENCE KV-7500：型式／状態／診断情報、DM／MR操作
- 三菱Q00UJCPU：型式／状態／診断情報、D／M操作

他機種での互換性は、MCプロトコル対応、デバイスマップ、Ethernet設定に依存します。
