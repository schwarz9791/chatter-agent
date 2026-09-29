package tech.sukima.chattermascot;

import android.util.Log;
import android.os.Process;

import com.unity3d.player.UnityPlayerGameActivity;

/**
 * Unity の GameActivity 入口はプロセスを生かしたままにし、onDestroy も戻ってこない（#156）。
 * プロセスが残ると次の起動はその中で Activity を作り直すことになり、Unity が
 * 再初期化できずスプラッシュのまま進まない。そのため Activity が破棄されたらすぐに
 * プロセスを終える（Unity 自身の後始末は、システムがタスクを殺す場合と同じく行わない）。
 */
public class ChatterMascotGameActivity extends UnityPlayerGameActivity
{
    private static final String TAG = "ChatterMascot";

    @Override
    protected void onDestroy()
    {
        // super.onDestroy() は Unity の終了待ちで UI スレッドを塞ぎ続けるため呼ばない。
        // 自プロセスへの SIGKILL は制御が戻る前に終わる。
        Log.i(TAG, "onDestroy: プロセスを終了します");
        Process.killProcess(Process.myPid());
    }
}
