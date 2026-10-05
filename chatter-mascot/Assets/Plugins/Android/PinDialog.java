package tech.sukima.chattermascot;

import android.app.Activity;
import android.app.AlertDialog;
import android.text.InputFilter;
import android.text.InputType;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.WindowManager;
import android.widget.EditText;

/**
 * 4 桁の PIN を受けるネイティブのダイアログ。
 *
 * ダイアログの操作はすべてこのクラスの中で完結させ、C# 側へは isDone() / take() の結果だけを返す。
 * UI スレッドと Unity のスレッドの両方から触るので、状態の読み書きは synchronized で守る。
 */
public final class PinDialog {
    private static final String TAG = "ChatterMascot";

    private static boolean done;
    /** 入力された文字列。取り消しは null のまま done だけが立つ。 */
    private static String text;

    /** 開いているダイアログ。UI スレッドでだけ読み書きする。 */
    private static AlertDialog current;

    private PinDialog() {}

    private static synchronized void reset() {
        done = false;
        text = null;
    }

    private static synchronized void finish(String value) {
        text = value;
        done = true;
    }

    public static void show(final Activity activity, final String title, final String message,
                            final String hint, final String ok, final String cancel,
                            final int maxLength) {
        reset();
        activity.runOnUiThread(() -> {
            try {
                final EditText input = new EditText(activity);
                input.setInputType(InputType.TYPE_CLASS_NUMBER);
                input.setFilters(new InputFilter[] {new InputFilter.LengthFilter(maxLength)});
                input.setHint(hint);

                AlertDialog dialog = new AlertDialog.Builder(activity)
                    .setTitle(title)
                    .setMessage(message)
                    .setView(input)
                    .setPositiveButton(ok, (d, which) -> {
                        current = null;
                        finish(input.getText().toString());
                    })
                    .setNegativeButton(cancel, (d, which) -> {
                        current = null;
                        finish(null);
                    })
                    .setOnCancelListener(d -> {
                        current = null;
                        finish(null);
                    })
                    .create();
                current = dialog;
                // 外を触っただけで終わらせない
                dialog.setCanceledOnTouchOutside(false);
                dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
                dialog.show();
                input.requestFocus();
            } catch (RuntimeException e) {
                Log.w(TAG, "PinDialog: ダイアログを出せません: " + e);
                finish(null);
            }
        });
    }

    /** 呼び手が待つのをやめるとき（入力が要らなくなったとき）に閉じる。 */
    public static void dismiss() {
        new Handler(Looper.getMainLooper()).post(() -> {
            if (current != null) {
                current.dismiss();
                current = null;
            }
            finish(null);
        });
    }

    public static synchronized boolean isDone() {
        return done;
    }

    /** 結果を返して消す。取り消し・まだ終わっていないときは null。 */
    public static synchronized String take() {
        String value = text;
        text = null;
        done = false;
        return value;
    }
}
