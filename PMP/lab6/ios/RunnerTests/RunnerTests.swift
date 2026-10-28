import Flutter
import XCTest
@testable import Runner

final class RunnerTests: XCTestCase {
  func testManufacturerAndUnknownMethod() {
    var response: Any?
    DeviceChannels.handleManufacturer(
      FlutterMethodCall(methodName: "getManufacturer", arguments: nil)
    ) { response = $0 }
    XCTAssertEqual(response as? String, "Apple")
    DeviceChannels.handleManufacturer(
      FlutterMethodCall(methodName: "unknownMethod", arguments: nil)
    ) { response = $0 }
    XCTAssertTrue((response as AnyObject?) === FlutterMethodNotImplemented)
  }

  func testInvalidArgumentsAreRejectedBeforeRequestingPermission() {
    let invalid: [Any] = [
      "not a map",
      ["hour": 24, "minute": 0, "label": "Coffee"],
      ["hour": 9, "minute": 60, "label": "Coffee"],
      ["hour": true, "minute": 42, "label": "Coffee"],
      ["hour": 9.5, "minute": 42, "label": "Coffee"],
      ["hour": "9", "minute": 42, "label": "Coffee"],
      ["hour": 9, "minute": 42],
      ["hour": 9, "minute": 42, "label": String(repeating: "☕", count: 81)],
    ]
    for arguments in invalid {
      var response: Any?
      DeviceChannels.handleAlarm(
        FlutterMethodCall(methodName: "openAlarm", arguments: arguments)
      ) { response = $0 }
      XCTAssertEqual((response as? FlutterError)?.code, "INVALID_ARGUMENT")
    }
  }

  func testAlarmUnknownMethodDoesNotSchedule() {
    var response: Any?
    DeviceChannels.handleAlarm(
      FlutterMethodCall(methodName: "unknownMethod", arguments: nil)
    ) { response = $0 }
    XCTAssertTrue((response as AnyObject?) === FlutterMethodNotImplemented)
  }

  func testNextOccurrenceIsTodayOrTomorrowAndUsesLocalCalendar() throws {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 3 * 3600))
    let request = try AlarmRequest(arguments: ["hour": 9, "minute": 42, "label": "Coffee"])
    let before = try XCTUnwrap(calendar.date(from:
      DateComponents(year: 2026, month: 10, day: 27, hour: 9, minute: 41)))
    let first = try XCTUnwrap(request.nextDate(after: before, calendar: calendar))
    XCTAssertEqual(calendar.component(.day, from: first), 27)
    XCTAssertEqual(calendar.component(.hour, from: first), 9)
    XCTAssertEqual(calendar.component(.minute, from: first), 42)
    let next = try XCTUnwrap(request.nextDate(after: first, calendar: calendar))
    XCTAssertEqual(calendar.component(.day, from: next), 28)
    XCTAssertEqual(calendar.component(.hour, from: next), 9)
    XCTAssertEqual(calendar.component(.minute, from: next), 42)
  }
}
