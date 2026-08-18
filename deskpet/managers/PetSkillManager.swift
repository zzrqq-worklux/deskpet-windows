//
//  PetSkillManager.swift
//  DeskPet
//
//  轻量 Skill 层：先处理确定性的桌面操作，再把普通问题交给 LLM。
//

import Foundation

struct PetSkillResult {
    let skillName: String
    let reply: String
}

@MainActor
final class PetSkillManager {
    static let shared = PetSkillManager()

    private var workStartedAt: Date?
    private var workReminderTimer: Timer?
    private let workReminderInterval: TimeInterval = 45 * 60
    private var lastReminderIdentifier: String?
    private var lastReminderDate: Date?

    var canUpdateLastReminder: Bool {
        lastReminderIdentifier != nil && lastReminderDate != nil
    }

    func updateLastReminder(hour: Int, minute: Int) async -> String {
        guard let identifier = lastReminderIdentifier, let oldDate = lastReminderDate else {
            return "执行失败：当前会话没有可修改的上一条提醒。"
        }
        let date = Calendar.current.date(bySettingHour: hour, minute: minute, second: 0, of: oldDate) ?? oldDate
        do {
            try await CalendarManager.shared.updateReminder(identifier: identifier, dueDate: date)
            lastReminderDate = date
            return "执行成功：提醒时间已修改为 \(formattedDate(date))。"
        } catch {
            return "执行失败：\(error.localizedDescription)"
        }
    }

    private init() {}

    /// 返回 nil 表示不是 Skill 指令，应继续交给 LLM。
    func handle(_ text: String) async -> PetSkillResult? {
        let input = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = input.lowercased()

        if isTimeQuery(lower) {
            return PetSkillResult(skillName: "time", reply: timeReply())
        }
        if isReminderUpdateCommand(lower) {
            return await updateReminderReply(input)
        }
        if isCalendarWriteCommand(lower) {
            return await calendarWriteReply(input)
        }
        if isCalendarQuery(lower) {
            return PetSkillResult(skillName: "calendar", reply: await calendarReply(input))
        }
        if isWorkCommand(lower) {
            return PetSkillResult(skillName: "work-companion", reply: workReply(lower))
        }
        if isMusicCommand(lower) {
            return PetSkillResult(skillName: "music", reply: await musicReply(lower))
        }
        if isRAGCommand(lower) {
            return await ragReply(input)
        }
        return nil
    }

    private func isTimeQuery(_ text: String) -> Bool {
        text.contains("几点") || text.contains("时间") || text.contains("日期") || text.contains("几号")
    }

    private func timeReply() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy年M月d日 EEEE HH:mm"
        return "现在是 \(formatter.string(from: Date()))。"
    }

    private func isCalendarQuery(_ text: String) -> Bool {
        let readWords = ["今天", "今日", "现在", "接下来", "最近", "下一个"]
        let calendarWords = ["日程", "会议", "提醒", "日历"]
        let monthQuery = text.range(of: #"(\d{1,4})\s*年?\s*(\d{1,2})\s*月"#, options: .regularExpression) != nil
        return (readWords.contains(where: { text.contains($0) }) || monthQuery)
            && calendarWords.contains(where: { text.contains($0) })
    }

    private func isCalendarWriteCommand(_ text: String) -> Bool {
        let writeWords = ["创建日历", "添加到日历", "写入日历", "新建日程", "创建提醒", "添加提醒", "设置提醒", "安排日程", "提醒我", "记住"]
        let hasDate = text.range(of: #"\d{1,4}\s*(?:年|[./-])?\s*\d{1,2}\s*(?:月|[./-])\s*\d{1,2}\s*[日号]?|\d{1,2}\s*[./-]\s*\d{1,2}"#, options: .regularExpression) != nil
        let hasRelativeDate = ["今天", "明天", "后天", "本月", "下月", "这个月", "下个月", "这周", "本周", "下周", "周一", "周二", "周三", "周四", "周五", "周六", "周日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"]
            .contains(where: { text.contains($0) })
        let hasWriteVerb = writeWords.contains(where: { text.contains($0) }) || text.contains("安排")
        return hasWriteVerb && (hasDate || hasRelativeDate)
    }

    private func isReminderUpdateCommand(_ text: String) -> Bool {
        let updateWords = ["改为", "改成", "修改提醒", "调整提醒", "把提醒"]
        return updateWords.contains(where: { text.contains($0) }) && timeInText(text) != nil
    }

    private func calendarReply(_ text: String) async -> String {
        let calendar = Calendar.current
        let range = calendarRange(from: text)
        let allEvents = await CalendarManager.shared.events(from: range.start, to: range.end)
        let wantsReminders = text.contains("提醒")
        let events = allEvents.filter { wantsReminders ? $0.type.isReminder : $0.type.isEvent }
            .sorted { $0.start < $1.start }
        guard !events.isEmpty else { return wantsReminders ? "这个时间范围没有找到提醒事项。" : "这个时间范围没有找到日历日程。" }
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        let list = events.prefix(30).map { event in
            let day = calendar.component(.day, from: event.start)
            return "\(day)日 \(formatter.string(from: event.start)) \(event.title)"
        }
        return "查询到 \(list.count) 条日程：\n" + list.joined(separator: "\n")
    }

    private func calendarWriteReply(_ text: String) async -> PetSkillResult {
        guard let start = dateFromText(text) else {
            return PetSkillResult(skillName: "calendar", reply: "请告诉我具体日期，例如：下个月1号提醒我回广州，或安排 2026年11月8日 14点 的项目会议。")
        }
        let title = extractedEventTitle(from: text) ?? "桌宠提醒"
        if text.contains("提醒") || text.contains("记住") {
            do {
                let identifier = try await CalendarManager.shared.createReminder(title: title, dueDate: start)
                lastReminderIdentifier = identifier
                lastReminderDate = start
                let timeNote = Calendar.current.component(.hour, from: start) == 9 ? "（未指定时间，默认上午9点）" : ""
                return PetSkillResult(skillName: "reminder", reply: "已经写入提醒事项：\(title)，\(formattedDate(start))\(timeNote)。")
            } catch {
                return PetSkillResult(skillName: "reminder", reply: "写入提醒事项失败：\(error.localizedDescription)。请检查系统提醒事项权限。")
            }
        }
        let end = start.addingTimeInterval(3600)
        do {
            try await CalendarManager.shared.createEvent(title: title, start: start, end: end)
            return PetSkillResult(skillName: "calendar", reply: "已经写入日历：\(title)，\(formattedDate(start))。默认时长 1 小时。")
        } catch {
            return PetSkillResult(skillName: "calendar", reply: "写入日历失败：\(error.localizedDescription)。请检查系统日历权限。")
        }
    }

    private func updateReminderReply(_ text: String) async -> PetSkillResult {
        guard let identifier = lastReminderIdentifier, let oldDate = lastReminderDate,
              let time = timeInText(text) else {
            return PetSkillResult(skillName: "reminder", reply: "我还没有找到本次会话里刚创建的提醒，请重新告诉我日期和提醒内容。")
        }
        let date = Calendar.current.date(bySettingHour: time.hour, minute: time.minute, second: 0, of: oldDate) ?? oldDate
        do {
            // EventKit 支持直接修改提醒时间，不需要删除再创建。
            try await CalendarManager.shared.updateReminder(identifier: identifier, dueDate: date)
            lastReminderDate = date
            return PetSkillResult(skillName: "reminder", reply: "已将上一条提醒直接改为 \(formattedDate(date))。")
        } catch {
            return PetSkillResult(skillName: "reminder", reply: "修改提醒失败：\(error.localizedDescription)。")
        }
    }

    private func calendarRange(from text: String) -> (start: Date, end: Date) {
        let calendar = Calendar.current
        let now = Date()
        let matches = text.range(of: #"(\d{1,4})\s*年?\s*(\d{1,2})\s*月|\d{1,2}\s*[./-]\s*\d{1,2}"#, options: .regularExpression)
        if let matches {
            let value = String(text[matches])
            let numbers = value.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
            let year = numbers.count > 1 && value.contains("年") ? numbers[0] : calendar.component(.year, from: now)
            let month = numbers.last ?? calendar.component(.month, from: now)
            let start = calendar.date(from: DateComponents(year: year, month: month, day: 1)) ?? calendar.startOfDay(for: now)
            let end = calendar.date(byAdding: .month, value: 1, to: start) ?? start.addingTimeInterval(31 * 86400)
            return (start, end)
        }
        let start = calendar.startOfDay(for: now)
        return (start, calendar.date(byAdding: .day, value: 1, to: start) ?? start.addingTimeInterval(86400))
    }

    private func dateFromText(_ text: String) -> Date? {
        let calendar = Calendar.current
        let now = Date()
        let normalizedText = normalizeChineseNumbers(text)

        // 相对日期：今天 / 明天 / 后天
        let relativeDays: Int?
        if normalizedText.contains("后天") { relativeDays = 2 }
        else if normalizedText.contains("明天") { relativeDays = 1 }
        else if normalizedText.contains("今天") { relativeDays = 0 }
        else { relativeDays = nil }

        var date: Date
        if text.contains("下个月") || text.contains("下月") {
            let numbers = normalizedText.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
            let day = numbers.last ?? 1
            let nextMonth = calendar.date(byAdding: .month, value: 1, to: calendar.startOfDay(for: now)) ?? now
            let components = calendar.dateComponents([.year, .month], from: nextMonth)
            date = calendar.date(from: DateComponents(year: components.year, month: components.month, day: day, hour: 9, minute: 0)) ?? nextMonth
        } else if text.contains("本月") || text.contains("这个月") {
            let numbers = normalizedText.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
            let day = numbers.last ?? 1
            let components = calendar.dateComponents([.year, .month], from: now)
            date = calendar.date(from: DateComponents(year: components.year, month: components.month, day: day, hour: 9, minute: 0)) ?? now
        } else if let relativeDays {
            date = calendar.date(byAdding: .day, value: relativeDays, to: calendar.startOfDay(for: now)) ?? now
        } else if let weekday = weekdayInText(normalizedText) {
            let today = calendar.component(.weekday, from: now)
            var delta = (weekday - today + 7) % 7
            if text.contains("下周") { delta += 7 }
            date = calendar.date(byAdding: .day, value: delta, to: calendar.startOfDay(for: now)) ?? now
        } else {
            let numbers = normalizedText.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
            guard numbers.count >= 2 else { return nil }
            let hasYear = numbers[0] > 1000
            let year = hasYear ? numbers[0] : calendar.component(.year, from: now)
            let month = hasYear ? numbers[1] : numbers[0]
            let day = hasYear ? numbers[2] : numbers[1]
            date = calendar.date(from: DateComponents(year: year, month: month, day: day, hour: 9, minute: 0)) ?? now
        }

        // 时间：下午3点 / 晚上8点 / 15:30 / 14点；未指定则默认 09:00
        if let time = timeInText(normalizedText) {
            date = calendar.date(bySettingHour: time.hour, minute: time.minute, second: 0, of: date) ?? date
        }
        return date
    }

    private func weekdayInText(_ text: String) -> Int? {
        let values: [(String, Int)] = [
            ("周日", 1), ("星期日", 1), ("周天", 1), ("星期天", 1),
            ("周一", 2), ("星期一", 2), ("周二", 3), ("星期二", 3),
            ("周三", 4), ("星期三", 4), ("周四", 5), ("星期四", 5),
            ("周五", 6), ("星期五", 6), ("周六", 7), ("星期六", 7),
        ]
        return values.first(where: { text.contains($0.0) })?.1
    }

    private func timeInText(_ text: String) -> (hour: Int, minute: Int)? {
        let normalized = normalizeChineseNumbers(text)
        guard let match = normalized.range(of: #"(上午|早上|下午|晚上|中午)?\s*(\d{1,2})(?::|点|时)(\d{1,2})?"#, options: .regularExpression) else {
            return nil
        }
        let value = String(normalized[match])
        let numbers = value.components(separatedBy: CharacterSet.decimalDigits.inverted).compactMap { Int($0) }
        guard let rawHour = numbers.first else { return nil }
        let minute = numbers.count > 1 ? numbers[1] : 0
        var hour = rawHour
        if (value.contains("下午") || value.contains("晚上")) && hour < 12 { hour += 12 }
        if value.contains("中午") && hour < 11 { hour += 12 }
        guard (0...23).contains(hour), (0...59).contains(minute) else { return nil }
        return (hour, minute)
    }

    /// 支持“下午五点”“十一月四日”等常见中文数字写法。
    private func normalizeChineseNumbers(_ text: String) -> String {
        var result = text
        let compounds = [
            "十一": "11", "十二": "12", "十三": "13", "十四": "14", "十五": "15",
            "十六": "16", "十七": "17", "十八": "18", "十九": "19"
        ]
        for (from, to) in compounds { result = result.replacingOccurrences(of: from, with: to) }
        let digits = ["零": "0", "一": "1", "二": "2", "三": "3", "四": "4", "五": "5", "六": "6", "七": "7", "八": "8", "九": "9", "十": "10"]
        for (from, to) in digits { result = result.replacingOccurrences(of: from, with: to) }
        return result
    }

    private func extractedEventTitle(from text: String) -> String? {
        if let reminderRange = text.range(of: #"(?:提醒我|记住)\s*(.+)$"#, options: .regularExpression) {
            let title = String(text[reminderRange])
                .replacingOccurrences(of: "提醒我", with: "")
                .replacingOccurrences(of: "记住", with: "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !title.isEmpty { return title }
        }
        guard let range = text.range(of: #"(?:的|：|:)(.+)$"#, options: .regularExpression) else { return nil }
        let title = String(text[range]).drop { $0 == "的" || $0 == "：" || $0 == ":" }.trimmingCharacters(in: .whitespacesAndNewlines)
        return title.isEmpty ? nil : title
    }

    private func formattedDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy年M月d日 HH:mm"
        return formatter.string(from: date)
    }

    private func isWorkCommand(_ text: String) -> Bool {
        text.contains("开始工作") || text.contains("开始计时") || text.contains("开始专注")
            || text.contains("停止工作") || text.contains("停止计时") || text.contains("工作多久")
            || text.contains("专注多久")
    }

    private func workReply(_ text: String) -> String {
        if text.contains("停止") {
            guard let started = workStartedAt else { return "还没有正在进行的工作计时。" }
            workStartedAt = nil
            workReminderTimer?.invalidate()
            workReminderTimer = nil
            return "这次专注了 \(formattedDuration(Date().timeIntervalSince(started)))。辛苦了，休息一下吧。"
        }
        if text.contains("多久") {
            guard let started = workStartedAt else { return "还没有开始工作计时。" }
            return "你已经连续工作 \(formattedDuration(Date().timeIntervalSince(started))) 了，记得起来活动一下。"
        }
        workStartedAt = Date()
        workReminderTimer?.invalidate()
        workReminderTimer = Timer.scheduledTimer(withTimeInterval: workReminderInterval, repeats: true) { [weak self] _ in
            guard let self else { return }
            PetBubbleManager.shared.showProactiveMessage("已经工作 45 分钟了，起来活动一下吧。别让脖子先报错。")
        }
        RunLoop.main.add(workReminderTimer!, forMode: .common)
        return "好，开始陪你工作。过一会儿我会提醒你休息。"
    }

    private func formattedDuration(_ seconds: TimeInterval) -> String {
        let minutes = max(1, Int(seconds / 60))
        if minutes < 60 { return "\(minutes) 分钟" }
        return "\(minutes / 60) 小时 \(minutes % 60) 分钟"
    }

    private func isMusicCommand(_ text: String) -> Bool {
        text.contains("播放音乐") || text.contains("暂停音乐") || text.contains("暂停")
            || text.contains("下一首") || text.contains("上一首") || text.contains("当前歌曲")
            || text.contains("现在放的")
    }

    private func musicReply(_ text: String) async -> String {
        let music = MusicManager.shared
        if text.contains("下一首") {
            music.nextTrack()
            return "切到下一首了。"
        }
        if text.contains("上一首") {
            music.previousTrack()
            return "回到上一首。"
        }
        if text.contains("播放") {
            music.play()
            return "开始播放。"
        }
        if text.contains("暂停") {
            music.pause()
            return "先暂停一下。"
        }
        // 网易云的控制接口和元数据接口更新时机不同，等待活动媒体控制器刷新。
        for _ in 0..<5 {
            if let track = await music.refreshCurrentTrack() {
                let artist = track.artist.isEmpty || track.artist == "Me" ? "未知歌手" : track.artist
                let state = track.isPlaying ? "正在播放" : "当前歌曲"
                return "\(state)是《\(track.title)》，歌手是 \(artist)。"
            }
            try? await Task.sleep(for: .milliseconds(250))
        }
        return "我暂时读不到网易云的歌曲元数据，但播放控制接口是正常的。请稍等片刻后再问一次。"
    }

    private func isRAGCommand(_ text: String) -> Bool {
        text.contains("搜索知识库") || text.contains("查知识库") || text.contains("查我的资料")
            || text.contains("从文档里找")
    }

    private func ragReply(_ query: String) async -> PetSkillResult? {
        guard VectorStoreManager.shared.isConfigured else {
            return PetSkillResult(skillName: "rag", reply: "知识库里还没有可检索的内容。先去 RAG 设置导入资料吧。")
        }
        guard let embedding = try? await PetChatManager.shared.embed([query]).first else {
            return PetSkillResult(skillName: "rag", reply: "我暂时无法生成检索向量，请检查 Embedding 模型和 Ollama 是否运行。")
        }
        let results = await VectorStoreManager.shared.search(query: query, embedding: embedding, topK: 3)
        guard !results.isEmpty else { return PetSkillResult(skillName: "rag", reply: "知识库里没有找到相关内容。") }
        let text = results.map { "• \($0.text.replacingOccurrences(of: "\n", with: " "))" }.joined(separator: "\n")
        return PetSkillResult(skillName: "rag", reply: "我找到这些相关内容：\n\(text)")
    }
}
