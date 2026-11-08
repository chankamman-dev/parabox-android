package dev.kmparabox;

import android.os.Bundle;
import android.util.Log;
import com.unity3d.player.UnityPlayerActivity;

// Unity's activity, with FMOD's Java half started first. The game's FMODUnity.dll was built for Windows,
// so the Android-only org.fmod.FMOD.init call it would normally make is not in it.
// The game's streams and sound banks are downloaded by the launcher (dev.kmparabox.cloud.GameData);
// Only the launcher starts this activity (not exported), and only once that data is in place.
public class ParaboxActivity extends UnityPlayerActivity {
    static final String TAG = "Parabox";

    @Override protected void onCreate(Bundle b) {
        try {
            System.loadLibrary("fmod");
            Class<?> c = Class.forName("org.fmod.FMOD");
            Object ok = c.getMethod("init", android.content.Context.class).invoke(null, this);
            Log.i(TAG, "org.fmod.FMOD.init -> " + ok);
        } catch (Throwable t) {
            Log.w(TAG, "FMOD Java side not started: " + t);
        }
        super.onCreate(b);
    }

    @Override protected void onDestroy() {
        try { Class.forName("org.fmod.FMOD").getMethod("close").invoke(null); } catch (Throwable t) { }
        super.onDestroy();
    }
}
