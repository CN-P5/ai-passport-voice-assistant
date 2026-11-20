namespace AiPassportVoice.Core.Input;

/// <summary>
/// 热键注入器。hold=按住期间保持按下、松开抬起；tap=点一次开启、再点一次结束。
/// </summary>
public interface IKeyInjector
{
    /// <summary>tap 模式是否处于「已开启」状态；hold 模式是否处于按住状态。</summary>
    bool IsActive { get; }

    /// <summary>设备开始说话：hold 按下不放 / tap 点击一次。</summary>
    void Start();

    /// <summary>设备结束说话：hold 抬起 / tap 再点击一次。</summary>
    void Stop();

    /// <summary>发送单次按键点击（用于回车等独立动作）。</summary>
    void Click();
}
