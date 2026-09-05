import Foundation
import XCTest

/// `shared/fixtures/v1` を読み込むヘルパ。fixture が唯一の正本。
enum SharedFixtures {
    static func load(_ name: String, version: Int = 1) throws -> [String: Any] {
        let url = directoryURL(version: version).appendingPathComponent("\(name).json")
        let data = try Data(contentsOf: url)
        let object = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        guard let dictionary = object as? [String: Any] else {
            throw FixtureError.invalidRoot(path: url.path)
        }
        return dictionary
    }

    static func section(
        _ fixture: String,
        _ section: String,
        version: Int = 1
    ) throws -> [[String: Any]] {
        let root = try load(fixture, version: version)
        guard let array = root[section] as? [Any] else {
            throw FixtureError.missingSection(fixture: fixture, section: section)
        }
        return try array.map { item in
            guard let object = item as? [String: Any] else {
                throw FixtureError.invalidCase(fixture: fixture, section: section, name: "<item>")
            }
            return object
        }
    }

    static func caseNames(
        _ fixture: String,
        _ section: String,
        version: Int = 1
    ) throws -> [String] {
        try self.section(fixture, section, version: version).map { item in
            guard let name = item["name"] as? String else {
                throw FixtureError.invalidCase(
                    fixture: fixture,
                    section: section,
                    name: "<item>"
                )
            }
            return name
        }
    }

    static func `case`(
        _ fixture: String,
        _ section: String,
        _ name: String,
        version: Int = 1
    ) throws -> [String: Any] {
        for item in try self.section(fixture, section, version: version) {
            guard let caseName = item["name"] as? String else {
                throw FixtureError.invalidCase(
                    fixture: fixture,
                    section: section,
                    name: "<item>"
                )
            }
            if caseName == name {
                return item
            }
        }
        throw FixtureError.invalidCase(fixture: fixture, section: section, name: name)
    }

    static func text(_ value: Any?) -> String {
        guard let string = value as? String else {
            fatalError("expected a string")
        }
        return string
    }

    static func optionalText(_ value: Any?) -> String? {
        guard let value, !(value is NSNull) else { return nil }
        return value as? String
    }

    static func number(_ value: Any?) -> Int {
        guard let result = optionalNumber(value) else {
            fatalError("expected a number")
        }
        return result
    }

    static func optionalNumber(_ value: Any?) -> Int? {
        guard let value, !(value is NSNull) else { return nil }

        if let number = value as? NSNumber {
            guard !isBooleanNumber(number) else { return nil }
            switch String(cString: number.objCType) {
            case "f", "d":
                return Int(exactly: number.doubleValue)
            case "C", "S", "I", "L", "Q":
                return Int(exactly: number.uint64Value)
            default:
                return Int(exactly: number.int64Value)
            }
        }

        if let int = value as? Int { return int }
        if let double = value as? Double { return Int(exactly: double) }
        return nil
    }

    static func real(_ value: Any?) -> Double {
        if let number = value as? NSNumber {
            guard !isBooleanNumber(number) else {
                fatalError("expected a number")
            }
            return number.doubleValue
        }

        switch value {
        case let double as Double:
            return double
        case let int as Int:
            return Double(int)
        default:
            fatalError("expected a number")
        }
    }

    static func flag(_ value: Any?) -> Bool {
        switch value {
        case let bool as Bool:
            return bool
        case let number as NSNumber:
            return number.boolValue
        default:
            fatalError("expected a bool")
        }
    }

    /// キー順を無視した JSON の意味比較。NSNull は nil と同一視する。
    static func jsonEquals(_ left: Any?, _ right: Any?) -> Bool {
        let normalizedLeft = normalize(left)
        let normalizedRight = normalize(right)
        switch (normalizedLeft, normalizedRight) {
        case (nil, nil):
            return true
        case (let leftString as String, let rightString as String):
            return leftString == rightString
        case (let leftBool as Bool, let rightBool as Bool):
            return leftBool == rightBool
        case (let leftNumber as NSNumber, let rightNumber as NSNumber):
            // Bool は NSNumber としても来るため、真偽だけ先に揃える。
            if isBooleanNumber(leftNumber) || isBooleanNumber(rightNumber) {
                return leftNumber.boolValue == rightNumber.boolValue
            }
            return leftNumber.isEqual(to: rightNumber)
        case (let leftArray as [Any], let rightArray as [Any]):
            guard leftArray.count == rightArray.count else { return false }
            return zip(leftArray, rightArray).allSatisfy { jsonEquals($0, $1) }
        case (let leftObject as [String: Any], let rightObject as [String: Any]):
            guard Set(leftObject.keys) == Set(rightObject.keys) else { return false }
            return leftObject.keys.allSatisfy { key in
                jsonEquals(leftObject[key] as Any?, rightObject[key] as Any?)
            }
        default:
            return false
        }
    }

    static func parseUTF8(_ data: Data) throws -> Any {
        try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
    }

    /// literal / repeat / concat 記法を展開する。
    static func fixtureString(_ value: Any) -> String {
        guard let object = value as? [String: Any] else {
            fatalError("unhandled text node")
        }
        if let literal = object["literal"] {
            return text(literal)
        }
        if let repeatUnit = object["repeat"] {
            let unit = text(repeatUnit)
            let count = number(object["count"])
            return String(repeating: unit, count: count)
        }
        if let concat = object["concat"] as? [Any] {
            return concat.map(fixtureString).joined()
        }
        fatalError("unhandled text node")
    }

    static var uiCatalogURL: URL {
        localesDirectoryURL.appendingPathComponent("ui.json")
    }

    static func uiCatalogJSON() throws -> Data {
        try Data(contentsOf: uiCatalogURL)
    }

    private static func directoryURL(version: Int) -> URL {
        findDirectory("shared/fixtures/v\(version)")
    }

    private static let localesDirectoryURL: URL = {
        findDirectory("shared/locales")
    }()

    private static func findDirectory(_ relativePath: String) -> URL {
        var url = URL(fileURLWithPath: #filePath)
        while url.pathComponents.count > 1 {
            url.deleteLastPathComponent()
            let candidate = url.appendingPathComponent(relativePath, isDirectory: true)
            var isDirectory: ObjCBool = false
            if FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory),
                isDirectory.boolValue
            {
                return candidate
            }
        }
        fatalError("\(relativePath) not found above \(#filePath)")
    }

    private static func normalize(_ value: Any?) -> Any? {
        guard let value else { return nil }
        if value is NSNull { return nil }
        if let dictionary = value as? NSDictionary {
            var result: [String: Any] = [:]
            for (key, nested) in dictionary {
                guard let stringKey = key as? String else { continue }
                result[stringKey] = nested
            }
            return result
        }
        if let array = value as? NSArray {
            return array.map { $0 as Any }
        }
        return value
    }

    private static func isBooleanNumber(_ number: NSNumber) -> Bool {
        CFGetTypeID(number) == CFBooleanGetTypeID()
    }

    enum FixtureError: Error {
        case invalidRoot(path: String)
        case missingSection(fixture: String, section: String)
        case invalidCase(fixture: String, section: String, name: String)
    }
}
