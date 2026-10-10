namespace Junevy.Communication.Channels;

/// <summary>
/// 分帧器与编码器的工厂。每条连接调用一次 <see cref="CreateDecoder"/>，获得独立的分帧器实例。
/// </summary>
public interface IFrameCodecFactory
{
    /// <summary>创建新的分帧器（每条连接一个实例）。</summary>
    /// <returns>分帧器。</returns>
    IFrameDecoder CreateDecoder();

    /// <summary>创建编码器。</summary>
    /// <returns>编码器。</returns>
    IFrameEncoder CreateEncoder();
}
