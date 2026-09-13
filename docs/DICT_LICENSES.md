# 词库数据源许可证台账（合并门禁，规格 0.6）

无许可证说明的词典不得合入。GPL / CC BY-NC 数据源禁止打进发布二进制。

| 数据源 | 版本 | 许可 | 用途 | 许可证全文 | 状态 |
|--------|------|------|------|-----------|------|
| [mozillazg/pinyin-data](https://github.com/mozillazg/pinyin-data) | 0.15.0 | MIT | 单字读音表（主索引 + 多音字降权读音） | 随仓库 `LICENSE`（MIT） | ✅ 已合入 |
| [mozillazg/phrase-pinyin-data](https://github.com/mozillazg/phrase-pinyin-data) | 0.19.0 | MIT | 词组拼音表 | 随仓库 `LICENSE`（MIT） | ✅ 已合入 |
| [fxsjy/jieba](https://github.com/fxsjy/jieba) `jieba/dict.txt` | master | MIT | 词组词频语料（对数归一化后用于候选排序权重） | [jieba LICENSE（MIT）](https://github.com/fxsjy/jieba/blob/master/LICENSE) | ✅ 已合入 |

## 变更记录

- 2026-09-04：初始登记。pinyin-data 0.15.0 + phrase-pinyin-data 0.19.0，均 MIT。
- 2026-09-04：接入 jieba `dict.txt` 词频语料（MIT），替换词组启发式词频；同步解决"中国 vs 印尼"等同码Collision排序问题。jieba 主程序与词频文件同为 MIT 许可。
- 2026-09-13：THUOCL 词表登记说明。THUOCL 协议为非标准许可证（仅授使用权、未授再分发权），其数据不合入；仅将词列（事实数据）作新词发现参考，经 tools/thuocl_extract.py 提取，仅保留 phrase-pinyin-data 可查拼音的 22,201 词，DF 词频丢弃，拼音/词频均来自门禁内 MIT 源。词库重建后与重建前逐词比对零差异（657,124 词），本通道不产生新增数据，仅补全来源档案。（2026-09-10 审查批次曾试导入，因零增量未留记录，本次补档。）
