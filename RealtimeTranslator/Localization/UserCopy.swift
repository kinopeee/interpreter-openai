import Foundation

/// 設定に保存する表示言語。翻訳ペア (`languagePair`) とは別。
enum UiLanguagePreference: String, Sendable, Equatable {
    case system
    case ja
    case en

    static func parse(_ wireValue: String?) -> UiLanguagePreference {
        switch wireValue {
        case "ja":
            return .ja
        case "en":
            return .en
        default:
            return .system
        }
    }

    func resolve(osLanguageCode: String?) -> UiLocale {
        switch self {
        case .ja:
            return .ja
        case .en:
            return .en
        case .system:
            return osLanguageCode == "ja" ? .ja : .en
        }
    }
}

/// カタログから引く解決済みロケール。ja 以外の OS は en。
enum UiLocale: String, Sendable, Equatable {
    case ja
    case en
}

/// ユーザー向け文言カタログ。ログや字幕本文ではない。
/// 起動時に 1 回ロードし、プロセス内で不変。テストの Current は常に ja。
struct UserCopy: Sendable {
    let locale: UiLocale

    private let primary: [String: String]
    private let english: [String: String]
    private let missingKeyHandler: (@Sendable (String) -> Void)?

    init(
        locale: UiLocale,
        primary: [String: String],
        english: [String: String],
        missingKeyHandler: (@Sendable (String) -> Void)? = nil
    ) {
        self.locale = locale
        self.primary = primary
        self.english = english
        self.missingKeyHandler = missingKeyHandler
    }

    func text(_ key: String) -> String {
        if let value = primary[key] {
            return value
        }

        logMissing(key)
        if let value = english[key] {
            return value
        }

        return key
    }

    func text(_ key: String, _ substitutions: [String: String]) -> String {
        var template = text(key)
        for (name, value) in substitutions {
            template = template.replacingOccurrences(of: "{\(name)}", with: value)
        }
        return template
    }

    static func parse(json: Data, locale: UiLocale, missingKeyHandler: (@Sendable (String) -> Void)? = nil) throws -> UserCopy {
        let tables = try readLocaleTables(json)
        let primary = locale == .ja ? tables.ja : tables.en
        return UserCopy(
            locale: locale,
            primary: primary,
            english: tables.en,
            missingKeyHandler: missingKeyHandler
        )
    }

    static func load(from url: URL, locale: UiLocale) throws -> UserCopy {
        try parse(json: Data(contentsOf: url), locale: locale)
    }

    static func duplicateKeys(in json: Data) throws -> [String] {
        var seen = Set<String>()
        var duplicates: [String] = []
        for item in try stringEntries(json) {
            let key = try requiredText(item, "key")
            if seen.contains(key) {
                duplicates.append(key)
            }
            seen.insert(key)
        }
        return duplicates
    }

    static func placeholderMismatches(in json: Data) throws -> [String] {
        var mismatches: [String] = []
        for item in try stringEntries(json) {
            let key = try requiredText(item, "key")
            if placeholderNames(try requiredText(item, "ja")) != placeholderNames(try requiredText(item, "en")) {
                mismatches.append(key)
            }
        }
        return mismatches
    }

    static func placeholderNames(_ text: String) -> Set<String> {
        var names = Set<String>()
        var search = text[...]
        while let start = search.firstIndex(of: "{") {
            let afterStart = search.index(after: start)
            guard let end = search[afterStart...].firstIndex(of: "}") else { break }
            let name = String(search[afterStart..<end])
            if isPlaceholderName(name) {
                names.insert(name)
            }
            search = search[search.index(after: end)...]
        }
        return names
    }

    private func logMissing(_ key: String) {
        missingKeyHandler?(key)
        #if DEBUG
        AppLogger.general.debug("UserCopy missing key: \(key, privacy: .public)")
        #endif
    }

    private static func readLocaleTables(_ json: Data) throws -> (ja: [String: String], en: [String: String]) {
        var ja: [String: String] = [:]
        var en: [String: String] = [:]
        for item in try stringEntries(json) {
            let key = try requiredText(item, "key")
            ja[key] = try requiredText(item, "ja")
            en[key] = try requiredText(item, "en")
        }
        return (ja, en)
    }

    private static func stringEntries(_ json: Data) throws -> [[String: Any]] {
        let object = try JSONSerialization.jsonObject(with: json, options: [.fragmentsAllowed])
        guard let catalog = object as? [String: Any], let strings = catalog["strings"] as? [Any] else {
            throw UserCopyError.missingStrings
        }
        return try strings.map { item in
            guard let entry = item as? [String: Any] else {
                throw UserCopyError.invalidEntry
            }
            return entry
        }
    }

    private static func requiredText(_ item: [String: Any], _ name: String) throws -> String {
        guard let text = item[name] as? String, !text.isEmpty else {
            throw UserCopyError.missingField(name)
        }
        return text
    }

    /// Windows / shared-contracts と同じ `[A-Za-z_][A-Za-z0-9_]*`。
    private static func isPlaceholderName(_ name: String) -> Bool {
        guard let first = name.utf8.first else { return false }
        let isStart = first == UInt8(ascii: "_")
            || (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(first)
            || (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(first)
        guard isStart else { return false }
        return name.utf8.dropFirst().allSatisfy { byte in
            byte == UInt8(ascii: "_")
                || (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte)
                || (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte)
                || (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte)
        }
    }
}

enum UserCopyError: Error {
    case missingStrings
    case invalidEntry
    case missingField(String)
    case catalogNotFound
}

enum UserCopyStore {
    private static let lock = NSLock()
    // 起動時に 1 回載せ、以降は読取のみ。テストは ja のまま切り替えない。
    nonisolated(unsafe) private static var installed: UserCopy?

    static var current: UserCopy {
        lock.lock()
        defer { lock.unlock() }
        if let installed {
            return installed
        }
        let loaded = loadJapaneseDefault()
        installed = loaded
        return loaded
    }

    static func install(_ copy: UserCopy) {
        lock.lock()
        installed = copy
        lock.unlock()
    }

    /// Production / TEST_HOST はバンドル内の `ui.json` のみ。リポジトリ上の `shared/` は読まない。
    static func catalogURL() -> URL? {
        Bundle.main.url(forResource: "ui", withExtension: "json")
    }

    private static func loadJapaneseDefault() -> UserCopy {
        if let url = catalogURL(), let copy = try? UserCopy.load(from: url, locale: .ja) {
            return copy
        }
        return UserCopy(locale: .ja, primary: [:], english: [:])
    }
}
