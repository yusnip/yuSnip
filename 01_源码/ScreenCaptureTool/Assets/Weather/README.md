# 中国大陆天气地区目录

`MainlandRegions.tsv` 内嵌 31 个省级单位、2849 个区县代表点。字段为行政代码、省、市、区县、WGS84 纬度、经度、拼音搜索词（制表符分隔）。目录无需网络，天气请求只发送选中区县的代表坐标，不读取设备位置。

- 来源：[AreaCity-JsSpider-StatsGov](https://github.com/xiangyuecn/AreaCity-JsSpider-StatsGov)，MIT 许可，全文见同目录 `AreaCity-LICENSE.txt`。
- 数据版本：`2025.251231.260403`（2026-04-03 发布），`ok_data_level3.csv` 与 `ok_geo.csv`。原始中心点为 GCJ-02，经离线迭代转换近似 WGS84；只用于区县尺度模型天气，不用于导航、边界判断或精确定位。
- 原始 `ok_geo.csv.7z` SHA256：`675c3e9b8dec6444994d3ef53259a145304f6a957db8a5e601e55c0b9e61e21a`。
- 原始 `ok_data_level3-4.csv.7z` SHA256：`0b181b4105c32b2631b1c8c8654859f10684c3748b188ebe08f342291dec1169`。
- 目录为版本快照，部分省直辖县级单位使用补齐的市级父节点；上游注明尚缺新疆和康县、和安县。更新应替换来源快照并重新生成，不能假造行政代码或坐标。

解压上述两个发布资产到独立临时目录后，在仓库根执行：

```powershell
python '02_构建工具/检查/生成天气地区目录.py' --source 'log/weather-source'
```

来源目录还需包含上游 `LICENSE`。生成脚本不会联网，不包含四级乡镇或边界多边形。Open-Meteo 天气数据及服务条款详见 `docs/天气接入核对与申请材料.md`；目录许可与天气服务许可分别适用。
