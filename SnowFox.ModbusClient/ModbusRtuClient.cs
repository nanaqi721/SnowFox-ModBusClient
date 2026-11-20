
using System.IO.Ports;

namespace SnowFox.ModbusClient;

/// <summary>
/// 提供基于串口的 Modbus RTU 客户端。
/// </summary>
public sealed class ModbusRtuClient : IDisposable
{
    // 当前客户端持有并管理的串口实例，生命周期与客户端一致。
    private readonly SerialPort _serialPort;
    // 表示当前客户端是否已经释放，用于防止重复释放或释放后继续使用。
    private bool _disposed;

    /// <summary>
    /// 获取当前串口是否已经打开。
    /// </summary>
    /// <remarks>
    /// 该属性只表示本地串口已打开，不表示 Modbus 从站一定在线。
    /// </remarks>
    public bool IsOpen => !_disposed && _serialPort.IsOpen;

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
    /// <remarks>
    /// 如果串口已经打开，则不执行重复打开操作。
    /// </remarks>
    /// <exception cref="ObjectDisposedException">当前客户端已经被释放。</exception>
    public void Open()
    {
        ThrowIfDisposed();

        if (_serialPort.IsOpen)
        {
            return;
        }

        _serialPort.Open();
    }

    /// <summary>
    /// 关闭串口，但不释放客户端，使串口之后仍可再次打开。
    /// </summary>
    /// <remarks>
    /// 如果串口未打开或客户端已经释放，则不执行任何操作。
    /// </remarks>
    public void Close()
    {
        if (_disposed || !_serialPort.IsOpen)
        {
            return;
        }

        _serialPort.Close();
    }

    /// <summary>
    /// 关闭串口并释放当前客户端持有的串口资源。
    /// </summary>
    /// <remarks>
    /// 该方法可安全地重复调用；客户端释放后不能再次打开串口。
    /// </remarks>
    public void Dispose() 
    {
        if (_disposed)
        {
            return;
        }

        _serialPort.Dispose();
        _disposed = true;

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


}
