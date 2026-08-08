# DiaryApp

工作日记桌面应用，基于 .NET 10.0 + Avalonia UI 构建。

## 配置文件加密

加密配置文件（如 `DiaryApp.config`）可以无需程序、仅通过 OpenSSL 命令行解密：

```bash
# 解密
openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 100000 -d \
    -in DiaryApp.config -pass pass:你的密码

# 加密（如需手动生成）
openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 100000 \
    -in 明文.json -out DiaryApp.config -pass pass:你的密码
```

**参数说明**

| 参数 | 含义 |
|------|------|
| `enc` | 对称加密子命令 |
| `-aes-256-cbc` | 算法 AES-256，CBC 模式 |
| `-md sha256` | 指定与程序一致的 PBKDF2 摘要算法 |
| `-pbkdf2` | 使用 PBKDF2 派生密钥 |
| `-iter 100000` | PBKDF2 迭代次数（与程序一致） |
| `-d` | 解密模式，不加为加密模式 |
| `-in <文件>` | 输入文件 |
| `-pass pass:<密码>` | 指定密码（也可用 `-pass file:<路径>` 从文件读取） |

> 迭代次数 100,000 遵循 OWASP/NIST 建议。可在程序中调整，但需与 `-iter` 保持一致。

## 快速开始

```bash
dotnet build DiaryApp.sln
dotnet run --project Diary.App
```
