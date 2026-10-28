import AlarmKit
import AppIntents
import CoreFoundation
import Flutter
import SwiftUI
import UIKit

final class DeviceChannels {
  private let manufacturerChannel: FlutterMethodChannel
  private let alarmChannel: FlutterMethodChannel
  private let batteryChannel: FlutterMethodChannel

  init(messenger: FlutterBinaryMessenger) {
    manufacturerChannel = FlutterMethodChannel(
      name: "by.bstu.lab6/device", binaryMessenger: messenger)
    alarmChannel = FlutterMethodChannel(
      name: "by.bstu.lab6/alarm", binaryMessenger: messenger)
    batteryChannel = FlutterMethodChannel(
      name: "by.bstu.lab6/battery", binaryMessenger: messenger)

    manufacturerChannel.setMethodCallHandler { call, result in
      Self.handleManufacturer(call, result: result)
    }
    alarmChannel.setMethodCallHandler { call, result in
      Self.handleAlarm(call, result: result)
    }
    batteryChannel.setMethodCallHandler { call, result in
      Self.handleBattery(call, result: result)
    }
  }

  static func handleBattery(_ call: FlutterMethodCall, result: FlutterResult) {
    guard call.method == "getBatteryReading" else {
      result(FlutterMethodNotImplemented)
      return
    }
    let device = UIDevice.current
    let wasMonitoring = device.isBatteryMonitoringEnabled
    device.isBatteryMonitoringEnabled = true
    defer { device.isBatteryMonitoringEnabled = wasMonitoring }
    let level = device.batteryLevel
    guard level.isFinite, (0...1).contains(level) else {
      result(FlutterError(
        code: "UNAVAILABLE", message: "Уровень заряда батареи недоступен.", details: nil))
      return
    }
    let state: String
    switch device.batteryState {
    case .charging: state = "charging"
    case .unplugged: state = "discharging"
    case .full: state = "full"
    case .unknown: state = "unknown"
    @unknown default: state = "unknown"
    }
    result(["level": Int((level * 100).rounded()), "state": state])
  }

  static func handleManufacturer(_ call: FlutterMethodCall, result: FlutterResult) {
    guard call.method == "getManufacturer" else {
      result(FlutterMethodNotImplemented)
      return
    }
    // iOS runs on Apple hardware. UIDevice.model is a model family, not a manufacturer.
    result("Apple")
  }

  static func handleAlarm(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
    guard call.method == "openAlarm" else {
      result(FlutterMethodNotImplemented)
      return
    }
    let request: AlarmRequest
    do {
      request = try AlarmRequest(arguments: call.arguments)
    } catch {
      result(FlutterError(
        code: "INVALID_ARGUMENT",
        message: "Часы: 0–23, минуты: 0–59, подпись: до 80 символов.", details: nil))
      return
    }
    guard #available(iOS 26.0, *) else {
      result(FlutterError(
        code: "UNSUPPORTED_IOS_VERSION",
        message: "Установка будильника через AlarmKit требует iOS 26 или новее.",
        details: nil))
      return
    }
    Task { @MainActor in
      await Self.schedule(request, result: result)
    }
  }

  @available(iOS 26.0, *)
  @MainActor
  private static func schedule(_ request: AlarmRequest, result: FlutterResult) async {
    let manager = AlarmManager.shared
    do {
      let authorization = try await manager.requestAuthorization()
      guard authorization == .authorized else {
        result(FlutterError(
          code: "ALARM_PERMISSION",
          message: "Разрешите будильники для Coffee Lab 6 в настройках iOS.", details: nil))
        return
      }
      // Calculate after authorization: the permission prompt may have taken time.
      guard let date = request.nextDate(after: Date()) else {
        result(FlutterError(
          code: "INVALID_ARGUMENT", message: "Не удалось определить время будильника.",
          details: nil))
        return
      }

      let title: LocalizedStringResource = "\(request.label.isEmpty ? "Coffee time" : request.label)"
      let alert: AlarmPresentation.Alert
      if #available(iOS 26.1, *) {
        // Since 26.1 the system supplies the Stop button automatically.
        alert = AlarmPresentation.Alert(title: title)
      } else {
        alert = AlarmPresentation.Alert(
          title: title,
          stopButton: AlarmButton(text: "Стоп", textColor: .white, systemImageName: "stop.fill"))
      }
      let attributes = AlarmAttributes<CoffeeAlarmMetadata>(
        presentation: AlarmPresentation(alert: alert),
        metadata: CoffeeAlarmMetadata(),
        tintColor: .orange)
      let id = UUID()
      // A scheduled alert only: no countdown, snooze or widget extension is required.
      let configuration = AlarmManager.AlarmConfiguration<CoffeeAlarmMetadata>.alarm(
        schedule: .fixed(date),
        attributes: attributes,
        stopIntent: StopCoffeeAlarmIntent(alarmID: id.uuidString),
        sound: .default)
      _ = try await manager.schedule(id: id, configuration: configuration)
      // Success is returned only after the OS accepts the alarm, not just permission.
      result(true)
    } catch {
      result(FlutterError(
        code: "ALARM_SCHEDULE_FAILED",
        message: "Не удалось установить будильник: \(error.localizedDescription)", details: nil))
    }
  }
}

/// Validation and calendar calculation are independent of permission prompts.
struct AlarmRequest {
  let hour: Int
  let minute: Int
  let label: String

  enum ValidationError: Error { case invalidArguments }

  init(arguments: Any?) throws {
    guard let values = arguments as? [String: Any],
      let hour = Self.integer(values["hour"], in: 0...23),
      let minute = Self.integer(values["minute"], in: 0...59),
      let label = values["label"] as? String,
      label.utf16.count <= 80
    else { throw ValidationError.invalidArguments }
    self.hour = hour
    self.minute = minute
    self.label = label
  }

  private static func integer(_ value: Any?, in range: ClosedRange<Int>) -> Int? {
    guard let number = value as? NSNumber,
      CFGetTypeID(number) != CFBooleanGetTypeID(),
      let integer = Int(exactly: number.doubleValue),
      range.contains(integer)
    else { return nil }
    return integer
  }

  func nextDate(after now: Date, calendar: Calendar = .current) -> Date? {
    calendar.nextDate(
      after: now,
      matching: DateComponents(hour: hour, minute: minute, second: 0),
      matchingPolicy: .nextTime,
      repeatedTimePolicy: .first,
      direction: .forward)
  }
}

@available(iOS 26.0, *)
private struct CoffeeAlarmMetadata: AlarmMetadata {}

@available(iOS 26.0, *)
struct StopCoffeeAlarmIntent: LiveActivityIntent {
  static var title: LocalizedStringResource = "Остановить будильник Coffee Lab 6"

  @Parameter(title: "Идентификатор будильника")
  var alarmID: String

  init() {}
  init(alarmID: String) { self.alarmID = alarmID }

  func perform() async throws -> some IntentResult {
    if let id = UUID(uuidString: alarmID) {
      try AlarmManager.shared.stop(id: id)
    }
    return .result()
  }
}
