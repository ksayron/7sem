package by.bstu.coffee_lab6

import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import android.os.Build
import android.os.BatteryManager
import android.content.Intent
import android.content.IntentFilter
import android.content.ActivityNotFoundException
import android.provider.AlarmClock

class MainActivity : FlutterActivity() {
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        val messenger = flutterEngine.dartExecutor.binaryMessenger
        MethodChannel(messenger, "by.bstu.lab6/battery").setMethodCallHandler { call, result ->
            if (call.method != "getBatteryReading") {
                result.notImplemented()
                return@setMethodCallHandler
            }
            // Read the system's latest sticky broadcast without keeping a receiver alive.
            val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
            val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
            if (battery == null) {
                result.error("UNAVAILABLE", "Уровень заряда батареи недоступен.", null)
                return@setMethodCallHandler
            }
            val state = when (battery.getIntExtra(BatteryManager.EXTRA_STATUS, -1)) {
                BatteryManager.BATTERY_STATUS_CHARGING -> "charging"
                BatteryManager.BATTERY_STATUS_DISCHARGING -> "discharging"
                BatteryManager.BATTERY_STATUS_FULL -> "full"
                BatteryManager.BATTERY_STATUS_NOT_CHARGING ->
                    if (battery.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0)
                        "connectedNotCharging" else "discharging"
                else -> "unknown"
            }
            result.success(mapOf("level" to (level * 200L / scale).toInt(), "state" to state))
        }
        MethodChannel(messenger, "by.bstu.lab6/device").setMethodCallHandler { call, result ->
            when (call.method) {
                "getManufacturer" -> result.success(Build.MANUFACTURER)
                else -> result.notImplemented()
            }
        }
        MethodChannel(messenger, "by.bstu.lab6/alarm").setMethodCallHandler { call, result ->
            if (call.method != "openAlarm") {
                result.notImplemented()
                return@setMethodCallHandler
            }
            val hour = call.argument<Int>("hour")
            val minute = call.argument<Int>("minute")
            val label = call.argument<String>("label")
            if (hour == null || minute == null || label == null ||
                hour !in 0..23 || minute !in 0..59 || label.length > 80) {
                result.error("INVALID_ARGUMENT", "Часы: 0–23, минуты: 0–59, подпись: до 80 символов.", null)
                return@setMethodCallHandler
            }
            val intent = Intent(AlarmClock.ACTION_SET_ALARM).apply {
                putExtra(AlarmClock.EXTRA_HOUR, hour)
                putExtra(AlarmClock.EXTRA_MINUTES, minute)
                putExtra(AlarmClock.EXTRA_MESSAGE, label)
                putExtra(AlarmClock.EXTRA_SKIP_UI, false)
            }
            try {
                startActivity(intent)
                // This confirms dispatch only; Clock owns the final alarm creation.
                result.success(true)
            } catch (e: ActivityNotFoundException) {
                result.error("NO_ALARM_APP", "Приложение часов с поддержкой будильника не найдено.", null)
            } catch (e: SecurityException) {
                result.error("ALARM_PERMISSION", "Нет разрешения на запуск будильника.", null)
            }
        }
    }
}
