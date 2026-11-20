namespace SnowFox.ModbusClient;

/// <summary>
/// 提供 ModbusRTU 帧所使用的 CRC16 计算与校验功能
/// </summary>
internal static class ModbusCrc16
{
    // CRC16 初始化值
    private const ushort InitialValue = 0xFFFF;

    // Modbus CRC16 使用的反转多项式
    private const ushort Polynomial = 0xA001;

    /// <summary>
    /// 计算指定数据的 Modbus CRC16 校验值。
    /// </summary>
    /// <param name="data">参与计算的数据，不包含 CRC 字节。</param>
    /// <returns>计算得到的 16 位 CRC 校验值。</returns>
    internal static ushort Calculate(ReadOnlySpan<byte> data)
    {
        // 每次计算都从 Modbus CRC16 规定的初始值开始
        ushort crc = InitialValue;

        // 按顺序处理数据中的每一个字节
        foreach (byte value in data)
        {
            // 将当前字节合并到 CRC 的低 8 位
            crc ^= value;

            // 一个字节有 8 位，因此需要执行 8 次移位运算
            for (int bit = 0; bit < 8; bit++)
            {
                // 在右移前记录最低位是否为 1
                bool leastSignificantBitIsSet = (crc & 0x0001) != 0;

                // CRC 整体向右移动一位
                crc >>= 1;

                // 如果移出的是 1，则与 Modbus CRC16 多项式进行异或
                if (leastSignificantBitIsSet) 
                {
                    crc ^= Polynomial;
                }
            }
        }

        // 所有字节处理完成后返回最终 CRC
        return crc;
    }

    /// <summary>
    /// 为指定数据追加 CRC 低字节和高字节，生成完整的 Modbus RTU 帧。
    /// </summary>
    /// <param name="data">需要追加 CRC 的数据。</param>
    /// <returns>包含原始数据和两个 CRC 字节的新数组。</returns>
    internal static byte[] Append(ReadOnlySpan<byte> data) 
    {
        // CRC 占两个字节，因此新数组长度要比原数据多 2
        byte[] frame = new byte[data.Length + 2];

        // 将原始数据复制到新数组的起始位置
        data.CopyTo(frame);

        // 只对原始数据计算 CRC，不包含预留的两个 CRC 字节
        ushort crc = Calculate(data);

        // Modbus RTU 规定 CRC 低字节在前、高字节在后
        frame[^2] = (byte)(crc & 0x00FF);
        frame[^1] = (byte)(crc >> 8);

        // 返回可以直接发送的完整 RTU 帧
        return frame;
    }

    /// <summary>
    /// 验证完整 Modbus RTU 帧末尾的 CRC 是否正确。
    /// </summary>
    /// <param name="frame">包含原始数据和两个 CRC 字节的完整帧。</param>
    /// <returns>CRC 正确时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</returns>
    internal static bool IsValid(ReadOnlySpan<byte> frame) 
    {
        // 一个有效的待校验数据至少要包含两个 CRC 字节
        if (frame.Length < 2)
        {
            return false;
        }

        // 排除帧末尾的两个 CRC 字节，重新计算其余数据的 CRC
        ushort expectedCrc = Calculate(frame[..^2]);

        // 将计算结果拆分成低字节和高字节
        byte expectedLowByte = (byte)(expectedCrc & 0x00FF);
        byte expectedHighByte = (byte)(expectedCrc >> 8);

        // 将计算结果与帧末尾携带的 CRC 逐字节比较
        return frame[^2] == expectedLowByte && frame[^1] == expectedHighByte;
    }
}
