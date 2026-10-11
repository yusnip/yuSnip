using System;
using System.IO;

namespace ScreenCaptureTool.Core.Io
{
    /// <summary>
    /// 向 PNG 文件追加一个 tEXt chunk（标准 ancillary chunk），用于写入自定义元数据。
    /// 实现遵循 PNG (ISO/IEC 15948 / W3C PNG) 规范：
    /// - 文件签名 8 字节：89 50 4E 47 0D 0A 1A 0A
    /// - chunk 布局：[Length:4][Type:4][Data:Length][CRC:4]
    /// - CRC 覆盖 Type + Data，多项式 0xEDB88320（反向），初值 0xFFFFFFFF，结果取反
    /// </summary>
    /// <remarks>
    /// 本类只负责把构造好的 chunk 字节插入到 IEND chunk 之前；keyword/value 的合法性由调用方保证。
    /// 元数据写入失败时只记录日志、不向上抛——它是附加信息，不能让存图主流程因写 chunk 失败而失败。
    /// </remarks>
    public static class PngChunkWriter
    {
        /// <summary>PNG 文件签名（前 8 字节）。</summary>
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>IEND chunk 的完整 12 字节（length=0 + type + CRC）。IEND 永远是文件最后一个 chunk。</summary>
        private static readonly byte[] IendChunk =
        {
            0x00, 0x00, 0x00, 0x00,             // length = 0
            0x49, 0x45, 0x4E, 0x44,             // "IEND"
            0xAE, 0x42, 0x60, 0x82,             // CRC of "IEND"
        };

        /// <summary>
        /// 向 PNG 文件追加一个 tEXt chunk，插入到 IEND 之前。
        /// 采用「临时文件 + 原子替换」写入，避免写一半损坏原文件。
        /// </summary>
        /// <param name="filePath">已保存好的 PNG 文件路径。</param>
        /// <param name="keyword">tEXt keyword，必须为 ASCII 且不含空格与 Null（1-79 字节）。</param>
        /// <param name="value">tEXt value，Latin-1 文本，不含 Null。</param>
        /// <returns>true 表示写入成功；false 表示文件不是有效 PNG 或写入失败（已记录日志）。</returns>
        public static bool WriteTextChunk(string filePath, string keyword, string value)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("文件路径为空。", nameof(filePath));
            if (string.IsNullOrEmpty(keyword)) throw new ArgumentException("keyword 为空。", nameof(keyword));

            try
            {
                byte[] original = File.ReadAllBytes(filePath);
                if (!IsValidPng(original))
                {
                    AppLogger.Warn("PngChunkWriter: 目标文件不是有效 PNG，跳过写入 tEXt chunk： " + filePath);
                    return false;
                }

                int iendIndex = FindIendIndex(original);
                if (iendIndex < 0)
                {
                    AppLogger.Warn("PngChunkWriter: 未找到 IEND chunk，跳过写入 tEXt chunk：" + filePath);
                    return false;
                }

                byte[] chunk = BuildTextChunk(keyword, value);

                string tempPath = filePath + ".tmp_chunk";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // 签名 + IEND 之前的所有 chunk + 新 tEXt chunk + IEND
                    fs.Write(original, 0, iendIndex);
                    fs.Write(chunk, 0, chunk.Length);
                    fs.Write(IendChunk, 0, IendChunk.Length);
                    int afterIend = iendIndex + IendChunk.Length;
                    if (afterIend < original.Length)
                    {
                        // IEND 之后理论上不应有数据（个别工具会追加 trailing），原样保留。
                        fs.Write(original, afterIend, original.Length - afterIend);
                    }
                    fs.Flush(true);
                }

                File.Copy(tempPath, filePath, overwrite: true);
                try { File.Delete(tempPath); } catch { /* 临时文件清理失败无所谓 */ }

                AppLogger.Info("PngChunkWriter: 已写入 tEXt chunk（keyword=" + keyword + "）→ " + filePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("PngChunkWriter: 写入 tEXt chunk 失败（" + filePath + "）：" + ex.Message);
                return false;
            }
        }

        /// <summary>校验文件头部是否为 PNG 签名。</summary>
        private static bool IsValidPng(byte[] data)
        {
            return data != null && data.Length >= PngSignature.Length && MatchesAt(data, 0, PngSignature);
        }

        /// <summary>定位 IEND chunk 起始字节索引（length 字段的起点）；找不到返回 -1。</summary>
        private static int FindIendIndex(byte[] data)
        {
            // PNG 规范要求 IEND 必须是文件最后一个 chunk，优先核对尾部 12 字节。
            int tail = data.Length - IendChunk.Length;
            if (tail >= PngSignature.Length && MatchesAt(data, tail, IendChunk))
            {
                return tail;
            }
            // 极少见：IEND 之后被某些工具追加了 trailing 数据，退化为全文正向扫描。
            for (int start = PngSignature.Length; start <= data.Length - IendChunk.Length; start++)
            {
                if (MatchesAt(data, start, IendChunk)) return start;
            }
            return -1;
        }

        /// <summary>检查 data[offset..] 是否与 pattern 完全相等。</summary>
        private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (data[offset + i] != pattern[i]) return false;
            }
            return true;
        }

        /// <summary>构造完整 tEXt chunk 字节：[Length:4]["tEXt":4][data:Length][CRC:4]。</summary>
        private static byte[] BuildTextChunk(string keyword, string value)
        {
            // data = keyword + 0x00 + value，全部 Latin-1（这里 keyword/value 都是 ASCII）。
            byte[] keywordBytes = System.Text.Encoding.ASCII.GetBytes(keyword);
            byte[] valueBytes = System.Text.Encoding.Latin1.GetBytes(value ?? string.Empty);

            byte[] data = new byte[keywordBytes.Length + 1 + valueBytes.Length];
            Buffer.BlockCopy(keywordBytes, 0, data, 0, keywordBytes.Length);
            data[keywordBytes.Length] = 0x00;
            Buffer.BlockCopy(valueBytes, 0, data, keywordBytes.Length + 1, valueBytes.Length);

            byte[] chunk = new byte[4 + 4 + data.Length + 4];
            WriteBigEndianUInt32(chunk, 0, (uint)data.Length);
            // Type "tEXt"
            chunk[4] = 0x74; chunk[5] = 0x45; chunk[6] = 0x58; chunk[7] = 0x74;
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);

            // CRC 覆盖 Type + Data
            uint crc = Crc32(chunk, 4, 4 + data.Length);
            WriteBigEndianUInt32(chunk, chunk.Length - 4, crc);
            return chunk;
        }

        private static void WriteBigEndianUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// PNG 标准要求的 CRC-32：多项式 0xEDB88320（反向）、初值 0xFFFFFFFF、结果取反。
        /// 使用按字节计算的逐位实现（无查找表），代码量最小且足够快（仅对几十字节 Type+Data 计算）。
        /// </summary>
        private static uint Crc32(byte[] data, int offset, int length)
        {
            const uint polynomial = 0xEDB88320u;
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)(-(int)(crc & 1u)); // crc&1 为 1 时 mask=0xFFFFFFFF，否则 0
                    crc = (crc >> 1) ^ (polynomial & mask);
                }
            }
            return ~crc;
        }
    }
}
