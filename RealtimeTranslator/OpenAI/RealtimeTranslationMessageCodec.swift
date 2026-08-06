import Foundation

enum RealtimeTranslationMessageCodec {
    static func encode(_ event: RealtimeTranslationClientEvent) throws -> Data {
        let object: [String: Any]
        switch event {
        case .sessionUpdate(let config):
            var audio: [String: Any] = [
                "output": [
                    "language": config.outputLanguage.rawValue,
                ],
            ]
            var input: [String: Any] = [:]
            if let model = config.inputTranscriptionModel {
                input["transcription"] = ["model": model]
            }
            if let noiseReduction = config.noiseReduction {
                input["noise_reduction"] = ["type": noiseReduction.rawValue]
            } else {
                input["noise_reduction"] = NSNull()
            }
            if !input.isEmpty {
                audio["input"] = input
            }
            object = [
                "type": "session.update",
                "session": [
                    "audio": audio,
                ],
            ]
        case .inputAudioBufferAppend(let base64Audio):
            object = [
                "type": "session.input_audio_buffer.append",
                "audio": base64Audio,
            ]
        case .sessionClose:
            object = [
                "type": "session.close",
            ]
        }
        return try JSONSerialization.data(withJSONObject: object, options: [])
    }

    static func decodeServerEvent(from data: Data) throws -> RealtimeTranslationServerEvent {
        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: data, options: [])
        } catch {
            // shared/fixtures の decodeFailures 契約: parser 例外は invalidMessage へ正規化する。
            throw RealtimeTranslationError.invalidMessage
        }
        guard let dictionary = object as? [String: Any],
              let type = dictionary["type"] as? String
        else {
            throw RealtimeTranslationError.invalidMessage
        }

        switch type {
        case "session.created":
            return .sessionCreated
        case "session.updated":
            return .sessionUpdated
        case "session.input_transcript.delta":
            return .inputTranscriptDelta(
                delta: dictionary["delta"] as? String ?? "",
                eventID: dictionary["event_id"] as? String,
                elapsedMs: intValue(dictionary["elapsed_ms"])
            )
        case "session.output_transcript.delta":
            return .outputTranscriptDelta(
                delta: dictionary["delta"] as? String ?? "",
                eventID: dictionary["event_id"] as? String,
                elapsedMs: intValue(dictionary["elapsed_ms"])
            )
        case "session.output_audio.delta":
            // 字幕MVPでは音声payloadをデコードしない。
            return .outputAudioDelta
        case "session.closed":
            return .sessionClosed
        case "error":
            let errorObject = dictionary["error"] as? [String: Any]
            let message = (errorObject?["message"] as? String)
                ?? (dictionary["message"] as? String)
                ?? RealtimeTranslationError.genericServerMessage
            let code = (errorObject?["code"] as? String)
                ?? (errorObject?["type"] as? String)
            return .error(message: message, code: code)
        default:
            return .unknown(type: type)
        }
    }

    private static func intValue(_ value: Any?) -> Int? {
        switch value {
        case let int as Int:
            return int
        case let number as NSNumber:
            return number.intValue
        case let double as Double:
            return Int(double)
        default:
            return nil
        }
    }
}
