
using System.IO.Ports;

namespace SnowFox.ModbusClient;

/// <summary>
/// 提供基于串口的 Modbus RTU 客户端。
/// </summary>
public sealed class ModbusRtuClient : IDisposable
{
    // 当前客户端持有并管理的串口实例，生命周期与客户端一致。
    private readonly SerialPort _serialPort;

    // 保证一次只能执行一个完整的串口操作，防止请求和响应相互交叉。
    private readonly object _syncRoot = new object();

    // 表示当前客户端是否已经释放，用于防止重复释放或释放后继续使用。
    private bool _disposed;

    // 读取输出线圈的功能码。
    private const byte ReadCoilsFunctionCode = 0x01;

    // 读取离散输入的功能码。
    private const byte ReadDiscreteInputsFunctionCode = 0x02;

    // 读取保持寄存器的功能码。
    private const byte ReadHoldingRegistersFunctionCode = 0x03;

    // 读取输入寄存器的功能码。
    private const byte ReadInputRegistersFunctionCode = 0x04;

    // 写入单个输出线圈的功能码。
    private const byte WriteSingleCoilFunctionCode = 0x05;

    // 写入单个保持寄存器的功能码。
    private const byte WriteSingleRegisterFunctionCode = 0x06;

    // 写入多个输出线圈的功能码。
    private const byte WriteMultipleCoilsFunctionCode = 0x0F;

    // 写入多个保持寄存器的功能码。
    private const byte WriteMultipleRegistersFunctionCode = 0x10;

    // 功能码0F单次最多写入1968个线圈。
    private const int MaxWriteMultipleCoilsQuantity = 1968;

    // 功能码10单次最多写入123个寄存器。
    private const int MaxWriteMultipleRegistersQuantity = 123;

    // 功能码03和04单次最多读取125个寄存器。
    private const ushort MaxReadRegistersQuantity = 125;

    // 功能码01和02单次最多读取2000个位。
    private const ushort MaxReadBitsQuantity = 2000;

    // 功能码05规定的线圈接通值和断开值。
    private const ushort CoilOnValue = 0xFF00;
    private const ushort CoilOffValue = 0x0000;





    /// <summary>
    /// 获取当前串口是否已经打开。
    /// </summary>
    /// <remarks>
    /// 该属性只表示本地串口已打开，不表示 Modbus 从站一定在线。
    /// </remarks>
    public bool IsOpen
    {
        get
        {
            lock(_syncRoot) 
            {
                return !_disposed && _serialPort.IsOpen;
            }
        }
    }

    /// <summary>
    /// 使用指定的串口通信参数初始化 Modbus RTU 客户端。
    /// </summary>
    /// <param name="portName">串口名称，例如 COM3。</param>
    /// <param name="baudRate">波特率，默认值为 9600。</param>
    /// <param name="parity">奇偶校验方式，默认不校验。</param>
    /// <param name="dataBits">数据位数，默认值为 8。</param>
    /// <param name="stopBits">停止位数，默认为一个停止位。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="portName"/> 为 null、空字符串或仅包含空白字符。
    /// </exception>
    public ModbusRtuClient(
        string portName,
        int baudRate = 9600,
        Parity parity = Parity.None,
        int dataBits = 8,
        StopBits stopBits = StopBits.One)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            /*
            if (portName is null)
                throw new ArgumentNullException(nameof(portName), "串口名称不能为 null");
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("串口名称不能为空白", nameof(portName));
            */
            throw new ArgumentException("串口名称不能为空",nameof(portName));
        }

        _serialPort = new SerialPort(
            portName,
            baudRate,
            parity,
            dataBits,
            stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };


    }

    /// <summary>
    /// 打开串口。
    /// </summary>
    public void Open()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_serialPort.IsOpen)
            {
                return;
            }

            _serialPort.Open();
        }
    }

    /// <summary>
    /// 关闭串口，但不释放客户端。
    /// </summary>
    public void Close()
    {
        lock (_syncRoot)
        {
            if (_disposed || !_serialPort.IsOpen)
            {
                return;
            }

            _serialPort.Close();
        }
    }

    /// <summary>
    /// 关闭串口并释放串口资源。
    /// </summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _serialPort.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 检查当前客户端是否已经释放。
    /// </summary>
    /// <exception cref="ObjectDisposedException">当前客户端已经被释放。</exception>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ModbusRtuClient));
        }
    }

    /// <summary>
    /// 发送不包含 CRC 的请求数据，并接收、验证完整的响应帧。
    /// </summary>
    /// <param name="requestData">
    /// 请求数据，包含从站地址、功能码和功能数据，但不包含 CRC。
    /// </param>
    /// <param name="expectedResponseLength">
    /// 正常响应的完整长度，包含从站地址、功能码、数据和 CRC。
    /// </param>
    /// <returns>经过 CRC、从站地址和功能码验证的完整响应帧。</returns>
    /// <exception cref="ArgumentException">
    /// 请求数据没有包含从站地址和功能码。
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 预期响应长度不符合 Modbus RTU 帧长度要求。
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// 当前客户端已经释放。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 串口未打开，或者从站返回 Modbus 异常响应。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 响应的 CRC、从站地址或功能码不正确。
    /// </exception>
    private byte[] ExecuteRequest(
        ReadOnlySpan<byte> requestData,
        int expectedResponseLength)
    {
        // 一个请求至少需要包含从站地址和功能码。
        if (requestData.Length < 2)
        {
            throw new ArgumentException(
                "请求数据必须包含从站地址和功能码。",
                nameof(requestData));
        }

        // Modbus RTU 地址、功能码和 CRC 等字段决定了帧的基本长度。
        // RTU ADU 的最大长度为 256 字节。
        if (expectedResponseLength is < 5 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedResponseLength),
                "预期响应长度必须在 5 到 256 字节之间。");
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (!_serialPort.IsOpen)
            {
                throw new InvalidOperationException("串口尚未打开。");
            }

            // 给请求数据追加 CRC，生成可以直接发送的完整 RTU 帧。
            byte[] requestFrame = ModbusCrc16.Append(requestData);

            // 清除可能残留的旧数据，避免把旧响应当成本次响应。
            _serialPort.DiscardInBuffer();

            // 发送完整请求帧。
            _serialPort.Write(
                requestFrame,
                offset: 0,
                count: requestFrame.Length);

            // 接收正常响应或长度为 5 字节的异常响应。
            byte[] responseFrame = ReadResponse(expectedResponseLength);

            // 必须先验证 CRC，验证通过后才能信任响应中的其他字段。
            if (!ModbusCrc16.IsValid(responseFrame))
            {
                throw new InvalidDataException("响应帧的 CRC 校验失败。");
            }

            // 响应必须来自请求指定的从站。
            if (responseFrame[0] != requestData[0])
            {
                throw new InvalidDataException(
                    $"响应从站地址不匹配，期望 0x{requestData[0]:X2}，" +
                    $"实际 0x{responseFrame[0]:X2}。");
            }

            byte requestFunction = requestData[1];
            byte responseFunction = responseFrame[1];

            // 异常响应的功能码等于原功能码加上最高位 0x80。
            if (responseFunction == (byte)(requestFunction | 0x80))
            {
                byte exceptionCode = responseFrame[2];

                throw new InvalidOperationException(
                    $"Modbus 从站返回异常，功能码 0x{requestFunction:X2}，" +
                    $"异常码 0x{exceptionCode:X2}。");
            }

            // 正常响应的功能码必须和请求功能码一致。
            if (responseFunction != requestFunction)
            {
                throw new InvalidDataException(
                    $"响应功能码不匹配，期望 0x{requestFunction:X2}，" +
                    $"实际 0x{responseFunction:X2}。");
            }

            return responseFrame;
        }
    }

    /// <summary>
    /// 根据响应功能码读取正常响应或 Modbus 异常响应。
    /// </summary>
    /// <param name="expectedResponseLength">正常响应的完整长度。</param>
    /// <returns>读取到的完整响应帧。</returns>
    private byte[] ReadResponse(int expectedResponseLength)
    {
        const int responseHeaderLength = 2;
        const int exceptionResponseLength = 5;

        // 先读取从站地址和功能码。
        byte[] header = new byte[responseHeaderLength];
        ReadExactly(header, offset: 0, count: header.Length);

        // 功能码最高位为 1，表示从站返回异常响应。
        bool isExceptionResponse = (header[1] & 0x80) != 0;

        int responseLength = isExceptionResponse
            ? exceptionResponseLength
            : expectedResponseLength;

        byte[] response = new byte[responseLength];

        // 将已经读取的地址和功能码复制到完整响应数组中。
        Buffer.BlockCopy(
            header,
            srcOffset: 0,
            response,
            dstOffset: 0,
            count: header.Length);

        // 继续读取剩余数据。
        ReadExactly(
            response,
            offset: header.Length,
            count: response.Length - header.Length);

        return response;
    }

    /// <summary>
    /// 循环读取串口，直到指定数量的字节全部读完。
    /// </summary>
    /// <param name="buffer">用于保存读取结果的数组。</param>
    /// <param name="offset">开始写入数组的位置。</param>
    /// <param name="count">需要读取的字节数量。</param>
    private void ReadExactly(byte[] buffer, int offset, int count)
    {
        int totalBytesRead = 0;

        while (totalBytesRead < count)
        {
            int bytesRead = _serialPort.Read(
                buffer,
                offset + totalBytesRead,
                count - totalBytesRead);

            // 防止底层意外返回 0 时进入无限循环。
            if (bytesRead == 0)
            {
                throw new IOException("串口没有返回可读取的数据。");
            }

            totalBytesRead += bytesRead;
        }
    }

    /// <summary>
    /// 从指定的 Modbus 从站读取一个或多个输出线圈。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">起始线圈协议地址。</param>
    /// <param name="quantity">需要读取的线圈数量，有效范围为 1～2000。</param>
    /// <returns>按照地址顺序返回线圈的通断状态。</returns>
    public bool[] ReadCoils(
        byte slaveAddress,
        ushort startAddress,
        ushort quantity)
    {
        return ReadBits(
            slaveAddress,
            ReadCoilsFunctionCode,
            startAddress,
            quantity);
    }

    /// <summary>
    /// 从指定的 Modbus 从站读取一个或多个离散输入。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">起始离散输入协议地址。</param>
    /// <param name="quantity">需要读取的离散输入数量，有效范围为 1～2000。</param>
    /// <returns>按照地址顺序返回离散输入的通断状态。</returns>
    public bool[] ReadDiscreteInputs(
        byte slaveAddress,
        ushort startAddress,
        ushort quantity)
    {
        return ReadBits(
            slaveAddress,
            ReadDiscreteInputsFunctionCode,
            startAddress,
            quantity);
    }

    /// <summary>
    /// 使用指定功能码读取一个或多个连续的位状态。
    /// </summary>
    /// <param name="slaveAddress">从站地址。</param>
    /// <param name="functionCode">读取位状态使用的功能码。</param>
    /// <param name="startAddress">起始位协议地址。</param>
    /// <param name="quantity">需要读取的位数量。</param>
    /// <returns>按照地址顺序返回解析后的位状态。</returns>
    private bool[] ReadBits(
        byte slaveAddress,
        byte functionCode,
        ushort startAddress,
        ushort quantity)
    {
        // 地址0用于广播，而广播请求不会返回响应，因此读取操作只能使用1～247。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        // 功能码01和02单次最多读取2000个位。
        if (quantity is < 1 or > MaxReadBitsQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                $"读取数量必须在 1 到 {MaxReadBitsQuantity} 之间。");
        }

        // 防止“起始地址 + 数量”超出16位Modbus地址范围。
        uint endAddress = (uint)startAddress + quantity - 1;

        if (endAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "读取范围超出了 Modbus 16 位地址空间。");
        }

        // 请求数据固定为6个字节，不包含CRC。
        byte[] requestData =
        {
            slaveAddress,
            functionCode,

            // 起始地址高字节、低字节。
            (byte)(startAddress >> 8),
            (byte)(startAddress & 0x00FF),

            // 位数量高字节、低字节。
            (byte)(quantity >> 8),
            (byte)(quantity & 0x00FF)
        };

        // 位状态按字节打包，不足8位时也需要占用一个完整字节。
        int expectedDataByteCount = (quantity + 7) / 8;

        // 响应组成：从站地址1 + 功能码1 + 字节数1 + 数据N + CRC2。
        int expectedResponseLength = 5 + expectedDataByteCount;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 响应的第3个字节表示后续位状态数据的字节数量。
        if (responseFrame[2] != expectedDataByteCount)
        {
            throw new InvalidDataException(
                $"响应数据长度不正确，期望 {expectedDataByteCount} 字节，" +
                $"实际 {responseFrame[2]} 字节。");
        }

        bool[] states = new bool[quantity];

        // Modbus将每8个位打包到一个字节中，并从最低位开始排列。
        for (int index = 0; index < quantity; index++)
        {
            int dataIndex = 3 + index / 8;
            int bitIndex = index % 8;

            states[index] =
                (responseFrame[dataIndex] & (1 << bitIndex)) != 0;
        }

        return states;
    }

    /// <summary>
    /// 从指定的 Modbus 从站读取一个或多个保持寄存器。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">起始寄存器协议地址。</param>
    /// <param name="quantity">需要读取的寄存器数量，有效范围为 1～125。</param>
    /// <returns>按照地址顺序返回读取到的寄存器值。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址、起始地址范围或寄存器数量不符合要求。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站返回的寄存器数据长度不符合请求要求。
    /// </exception>
    public ushort[] ReadHoldingRegisters(
        byte slaveAddress,
        ushort startAddress,
        ushort quantity)
    {
        return ReadRegisters(
            slaveAddress,
            ReadHoldingRegistersFunctionCode,
            startAddress,
            quantity);
    }

    /// <summary>
    /// 从指定的 Modbus 从站读取一个或多个输入寄存器。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">起始输入寄存器协议地址。</param>
    /// <param name="quantity">需要读取的寄存器数量，有效范围为 1～125。</param>
    /// <returns>按照地址顺序返回读取到的输入寄存器值。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址、起始地址范围或寄存器数量不符合要求。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站返回的寄存器数据长度不符合请求要求。
    /// </exception>
    public ushort[] ReadInputRegisters(
        byte slaveAddress,
        ushort startAddress,
        ushort quantity)
    {
        return ReadRegisters(
            slaveAddress,
            ReadInputRegistersFunctionCode,
            startAddress,
            quantity);
    }

    /// <summary>
    /// 使用指定功能码读取一个或多个连续的16位寄存器。
    /// </summary>
    /// <param name="slaveAddress">从站地址。</param>
    /// <param name="functionCode">读取寄存器使用的功能码。</param>
    /// <param name="startAddress">起始寄存器协议地址。</param>
    /// <param name="quantity">需要读取的寄存器数量。</param>
    /// <returns>按照地址顺序返回解析后的寄存器值。</returns>
    private ushort[] ReadRegisters(
        byte slaveAddress,
        byte functionCode,
        ushort startAddress,
        ushort quantity)
    {
        // 地址0用于广播，而广播请求不会返回响应，因此读取操作只能使用1～247。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        // 功能码03和04单次最多读取125个寄存器。
        if (quantity is < 1 or > MaxReadRegistersQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                $"读取数量必须在 1 到 {MaxReadRegistersQuantity} 之间。");
        }

        // 防止“起始地址 + 数量”超出16位Modbus地址范围。
        uint endAddress = (uint)startAddress + quantity - 1;

        if (endAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "读取范围超出了 Modbus 16 位地址空间。");
        }

        // 请求数据固定为6个字节，不包含CRC。
        byte[] requestData =
        {
            slaveAddress,
            functionCode,

            // 起始地址高字节、低字节。
            (byte)(startAddress >> 8),
            (byte)(startAddress & 0x00FF),

            // 寄存器数量高字节、低字节。
            (byte)(quantity >> 8),
            (byte)(quantity & 0x00FF)
        };

        // 每个寄存器占两个字节。
        int expectedDataByteCount = quantity * 2;

        // 响应组成：从站地址1 + 功能码1 + 字节数1 + 数据N + CRC2。
        int expectedResponseLength = 5 + expectedDataByteCount;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 响应的第3个字节表示后续寄存器数据的字节数量。
        if (responseFrame[2] != expectedDataByteCount)
        {
            throw new InvalidDataException(
                $"响应数据长度不正确，期望 {expectedDataByteCount} 字节，" +
                $"实际 {responseFrame[2]} 字节。");
        }

        ushort[] registers = new ushort[quantity];

        // 寄存器数据从响应帧索引3开始，每个寄存器占两个字节。
        for (int index = 0; index < quantity; index++)
        {
            int dataIndex = 3 + index * 2;
            byte highByte = responseFrame[dataIndex];
            byte lowByte = responseFrame[dataIndex + 1];

            // Modbus寄存器内部使用高字节在前的顺序。
            registers[index] = (ushort)(
                (highByte << 8) |
                lowByte);
        }

        return registers;
    }

    /// <summary>
    /// 向指定 Modbus 从站写入一个输出线圈。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="coilAddress">需要写入的线圈协议地址。</param>
    /// <param name="value"><see langword="true"/> 表示接通，<see langword="false"/> 表示断开。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址不在 1～247 范围内。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站响应中回显的线圈地址或写入值与请求不一致。
    /// </exception>
    public void WriteSingleCoil(
        byte slaveAddress,
        ushort coilAddress,
        bool value)
    {
        // 地址0是广播地址，不会返回响应。
        // 当前ExecuteRequest必须等待响应，因此这里只允许1～247。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        // 功能码05只接受FF00（接通）或0000（断开）。
        ushort encodedValue = value
            ? CoilOnValue
            : CoilOffValue;

        byte[] requestData =
        {
            slaveAddress,
            WriteSingleCoilFunctionCode,

            // 线圈地址高字节、低字节。
            (byte)(coilAddress >> 8),
            (byte)(coilAddress & 0x00FF),

            // 写入值高字节、低字节。
            (byte)(encodedValue >> 8),
            (byte)(encodedValue & 0x00FF)
        };

        // 正常响应会完整回显请求，包含CRC时固定为8个字节。
        const int expectedResponseLength = 8;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 从响应中还原从站回显的线圈地址。
        ushort echoedCoilAddress = (ushort)(
            (responseFrame[2] << 8) |
            responseFrame[3]);

        // 从响应中还原从站回显的写入值。
        ushort echoedValue = (ushort)(
            (responseFrame[4] << 8) |
            responseFrame[5]);

        if (echoedCoilAddress != coilAddress)
        {
            throw new InvalidDataException(
                $"响应线圈地址不一致，期望 0x{coilAddress:X4}，" +
                $"实际 0x{echoedCoilAddress:X4}。");
        }

        if (echoedValue != encodedValue)
        {
            throw new InvalidDataException(
                $"响应线圈值不一致，期望 0x{encodedValue:X4}，" +
                $"实际 0x{echoedValue:X4}。");
        }
    }

    /// <summary>
    /// 向指定 Modbus 从站写入多个连续的输出线圈。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">开始写入的线圈协议地址。</param>
    /// <param name="values">按照地址顺序写入的线圈状态。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="values"/> 为 null。
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址、写入数量或线圈地址范围不符合要求。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站响应中的起始地址或写入数量与请求不一致。
    /// </exception>
    public void WriteMultipleCoils(
        byte slaveAddress,
        ushort startAddress,
        bool[] values)
    {
        // 地址0用于广播且不会返回响应，当前方法必须等待响应。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        int quantity = values.Length;

        // 功能码0F单次最多写入1968个线圈。
        if (quantity is < 1 or > MaxWriteMultipleCoilsQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"写入数量必须在 1 到 {MaxWriteMultipleCoilsQuantity} 之间。");
        }

        // 防止“起始地址 + 写入数量”超出16位Modbus地址范围。
        uint endAddress = (uint)startAddress + (uint)quantity - 1;

        if (endAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                "写入范围超出了 Modbus 16 位地址空间。");
        }

        // 每8个线圈状态打包成一个字节，不足8个时也占用一个完整字节。
        int dataByteCount = (quantity + 7) / 8;

        // 请求结构：从站地址1 + 功能码1 + 起始地址2 + 数量2
        // + 数据字节数1 + 线圈状态数据N。
        byte[] requestData = new byte[7 + dataByteCount];

        requestData[0] = slaveAddress;
        requestData[1] = WriteMultipleCoilsFunctionCode;
        requestData[2] = (byte)(startAddress >> 8);
        requestData[3] = (byte)(startAddress & 0x00FF);
        requestData[4] = (byte)(quantity >> 8);
        requestData[5] = (byte)(quantity & 0x00FF);
        requestData[6] = (byte)dataByteCount;

        // Modbus规定第一个线圈放在第一个数据字节的最低位。
        for (int index = 0; index < quantity; index++)
        {
            if (!values[index])
            {
                continue;
            }

            int dataIndex = 7 + index / 8;
            int bitIndex = index % 8;

            requestData[dataIndex] |= (byte)(1 << bitIndex);
        }

        // 正常响应只回显起始地址和写入数量，包含CRC时固定为8个字节。
        const int expectedResponseLength = 8;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 从响应中还原从站回显的起始地址。
        ushort echoedStartAddress = (ushort)(
            (responseFrame[2] << 8) |
            responseFrame[3]);

        // 从响应中还原从站回显的写入数量。
        ushort echoedQuantity = (ushort)(
            (responseFrame[4] << 8) |
            responseFrame[5]);

        if (echoedStartAddress != startAddress)
        {
            throw new InvalidDataException(
                $"响应起始地址不一致，期望 0x{startAddress:X4}，" +
                $"实际 0x{echoedStartAddress:X4}。");
        }

        if (echoedQuantity != quantity)
        {
            throw new InvalidDataException(
                $"响应写入数量不一致，期望 {quantity}，" +
                $"实际 {echoedQuantity}。");
        }
    }

    /// <summary>
    /// 向指定 Modbus 从站写入一个保持寄存器。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="registerAddress">需要写入的保持寄存器协议地址。</param>
    /// <param name="value">需要写入的16位无符号值。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址不在 1～247 范围内。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站响应中返回的寄存器地址或数值与请求不一致。
    /// </exception>
    public void WriteSingleRegister(
        byte slaveAddress,
        ushort registerAddress,
        ushort value)
    {
        // 地址0是广播地址，不会返回响应。
        // 当前ExecuteRequest必须等待响应，因此这里只允许1～247。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        // 功能码06请求固定包含：
        // 从站地址、功能码、寄存器地址和寄存器值。
        byte[] requestData =
        {
        slaveAddress,
        WriteSingleRegisterFunctionCode,

        // 寄存器地址高字节、低字节。
        (byte)(registerAddress >> 8),
        (byte)(registerAddress & 0x00FF),

        // 寄存器值高字节、低字节。
        (byte)(value >> 8),
        (byte)(value & 0x00FF)
    };

        // 正常响应会完整回显请求：
        // 地址1 + 功能码1 + 寄存器地址2 + 寄存器值2 + CRC2。
        const int expectedResponseLength = 8;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 从响应中还原从站回显的寄存器地址。
        ushort echoedRegisterAddress = (ushort)(
            (responseFrame[2] << 8) |
            responseFrame[3]);

        // 从响应中还原从站回显的寄存器值。
        ushort echoedValue = (ushort)(
            (responseFrame[4] << 8) |
            responseFrame[5]);

        // 地址不一致，说明响应不是当前写入操作的正确确认。
        if (echoedRegisterAddress != registerAddress)
        {
            throw new InvalidDataException(
                $"响应寄存器地址不一致，期望 0x{registerAddress:X4}，" +
                $"实际 0x{echoedRegisterAddress:X4}。");
        }

        // 数值不一致，说明从站没有正确确认本次写入。
        if (echoedValue != value)
        {
            throw new InvalidDataException(
                $"响应寄存器值不一致，期望 0x{value:X4}，" +
                $"实际 0x{echoedValue:X4}。");
        }
    }

    /// <summary>
    /// 向指定 Modbus 从站写入多个连续的保持寄存器。
    /// </summary>
    /// <param name="slaveAddress">从站地址，有效范围为 1～247。</param>
    /// <param name="startAddress">开始写入的保持寄存器协议地址。</param>
    /// <param name="values">按照地址顺序写入的寄存器值。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="values"/> 为 null。
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 从站地址、写入数量或寄存器范围不符合要求。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// 从站响应中的起始地址或写入数量与请求不一致。
    /// </exception>
    public void WriteMultipleRegisters(
        byte slaveAddress,
        ushort startAddress,
        ushort[] values)
    {
        // 地址0用于广播且不会返回响应，当前方法必须等待响应。
        if (slaveAddress is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveAddress),
                "从站地址必须在 1 到 247 之间。");
        }

        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        int quantity = values.Length;

        // 功能码10单次最多写入123个寄存器。
        if (quantity is < 1 or > MaxWriteMultipleRegistersQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"写入数量必须在 1 到 {MaxWriteMultipleRegistersQuantity} 之间。");
        }

        // 防止“起始地址 + 写入数量”超出16位地址范围。
        uint endAddress = (uint)startAddress + (uint)quantity - 1;

        if (endAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                "写入范围超出了 Modbus 16 位地址空间。");
        }

        // 每个寄存器占两个字节。
        int dataByteCount = quantity * 2;

        // 请求结构：
        // 从站地址1 + 功能码1 + 起始地址2 + 数量2
        // + 数据字节数1 + 寄存器数据N。
        byte[] requestData = new byte[7 + dataByteCount];

        requestData[0] = slaveAddress;
        requestData[1] = WriteMultipleRegistersFunctionCode;

        // 起始地址，高字节在前。
        requestData[2] = (byte)(startAddress >> 8);
        requestData[3] = (byte)(startAddress & 0x00FF);

        // 写入数量，高字节在前。
        requestData[4] = (byte)(quantity >> 8);
        requestData[5] = (byte)(quantity & 0x00FF);

        // 后续寄存器数据占用的字节数。
        requestData[6] = (byte)dataByteCount;

        // 从索引7开始依次写入每个寄存器的高、低字节。
        for (int index = 0; index < quantity; index++)
        {
            int dataIndex = 7 + index * 2;
            ushort value = values[index];

            requestData[dataIndex] = (byte)(value >> 8);
            requestData[dataIndex + 1] = (byte)(value & 0x00FF);
        }

        // 正常响应固定为：
        // 地址1 + 功能码1 + 起始地址2 + 写入数量2 + CRC2。
        const int expectedResponseLength = 8;

        byte[] responseFrame = ExecuteRequest(
            requestData,
            expectedResponseLength);

        // 读取响应回显的起始地址。
        ushort echoedStartAddress = (ushort)(
            (responseFrame[2] << 8) |
            responseFrame[3]);

        // 读取响应回显的写入数量。
        ushort echoedQuantity = (ushort)(
            (responseFrame[4] << 8) |
            responseFrame[5]);

        if (echoedStartAddress != startAddress)
        {
            throw new InvalidDataException(
                $"响应起始地址不一致，期望 0x{startAddress:X4}，" +
                $"实际 0x{echoedStartAddress:X4}。");
        }

        if (echoedQuantity != quantity)
        {
            throw new InvalidDataException(
                $"响应写入数量不一致，期望 {quantity}，" +
                $"实际 {echoedQuantity}。");
        }
    }
}
