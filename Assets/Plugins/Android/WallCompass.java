package com.arnav.walldistance;

import android.app.Activity;
import android.content.Context;
import android.hardware.*;
import android.os.SystemClock;

/** Magnetic bearing of the rear camera, independent of portrait/landscape UI rotation. */
public final class WallCompass implements SensorEventListener {
    private final SensorManager manager;
    private final float[] matrix = new float[9];
    private volatile float heading = Float.NaN;
    private volatile long updated;
    public WallCompass(Activity activity) {
        manager = (SensorManager)activity.getSystemService(Context.SENSOR_SERVICE);
        Sensor sensor = manager.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR);
        if (sensor != null) manager.registerListener(this, sensor, SensorManager.SENSOR_DELAY_GAME);
    }
    public void onSensorChanged(SensorEvent event) {
        if (event.accuracy == SensorManager.SENSOR_STATUS_UNRELIABLE) { heading=Float.NaN; return; }
        SensorManager.getRotationMatrixFromVector(matrix, event.values);
        // Android world axes are East, magnetic North, Up. Rear camera looks down
        // device -Z, so the negative third matrix column is its world direction.
        float east=-matrix[2], north=-matrix[5];
        heading=east*east+north*north<0.0225f ? Float.NaN :
            (float)((Math.toDegrees(Math.atan2(east,north))+360.0)%360.0);
        updated=SystemClock.elapsedRealtime();
    }
    public void onAccuracyChanged(Sensor sensor,int accuracy) {
        if (accuracy==SensorManager.SENSOR_STATUS_UNRELIABLE) heading=Float.NaN;
    }
    public float getHeading() { return SystemClock.elapsedRealtime()-updated<1500 ? heading : Float.NaN; }
    public void stop() { manager.unregisterListener(this); heading=Float.NaN; }
}
