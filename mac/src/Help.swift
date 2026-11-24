import AppKit

struct HelpTopic {
    let title: String
    var lines: [String]

    var plainText: String {
        return ([title] + lines).joined(separator: "\n")
            .replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
    }
}

/// Reads help.md (copied into the app by build.sh; shared with Windows) and keeps the Mac parts.
enum HelpContent {
    static func load() -> [HelpTopic] {
        guard let url = Bundle.main.url(forResource: "help", withExtension: "md"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            return [HelpTopic(title: "Help", lines: ["The guide couldn't be found. See github.com/jvbalcita/kapeyn."])]
        }
        return parse(text)
    }

    static func parse(_ text: String) -> [HelpTopic] {
        var topics: [HelpTopic] = []
        for raw in text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            var line = raw.replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
            if line.hasPrefix("%%") || line.hasPrefix("[win] ") { continue }
            if line.hasPrefix("[mac] ") { line = String(line.dropFirst(6)) }
            line = expand(line)
            if line.hasPrefix("# ") {
                topics.append(HelpTopic(title: String(line.dropFirst(2)), lines: []))
            } else if !topics.isEmpty {
                topics[topics.count - 1].lines.append(line)
            }
        }
        return topics
    }

    static func expand(_ s: String) -> String {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        return s.replacingOccurrences(of: "{computer}", with: "Mac")
            .replacingOccurrences(of: "{Computer}", with: "Mac")
            .replacingOccurrences(of: "{shortcut}", with: "Control-Option-K (⌃⌥K)")
            .replacingOccurrences(of: "{tray}", with: "menu bar (top right of the screen)")
            .replacingOccurrences(of: "{rightclick}", with: "right-click (or Control-click)")
            .replacingOccurrences(of: "{RightClick}", with: "Right-click (or Control-click)")
            .replacingOccurrences(of: "{startup}", with: "Start at login")
            .replacingOccurrences(of: "{version}", with: version)
    }
}

/// The Help window: topics (with search) on the left, the selected topic on the right.
final class HelpWindowController: NSObject, NSWindowDelegate {
    static var shared: HelpWindowController?

    private let topics = HelpContent.load()
    private var shown: [Int] = []          // indices of topics matching the search
    private var selected = 0
    private let window: NSWindow
    private let textView = NSTextView()
    private let search = NSSearchField()
    private let list = NSStackView()
    private let accent = NSColor(srgbRed: 0.69, green: 0.36, blue: 0.08, alpha: 1)

    static func show(topic: String?) {
        if shared == nil { shared = HelpWindowController() }
        shared?.select(title: topic)
        NSApp.activate(ignoringOtherApps: true)
        shared?.window.makeKeyAndOrderFront(nil)
    }

    private override init() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 880, height: 600),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        super.init()
        window.title = "Kapeyn Help"
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 620, height: 400)
        window.delegate = self
        window.center()

        // Left column: search + topic buttons
        search.placeholderString = "Search help"
        search.target = self
        search.action = #selector(searchChanged)
        search.sendsSearchStringImmediately = true
        list.orientation = .vertical
        list.alignment = .leading
        list.spacing = 2

        let listScroll = NSScrollView()
        listScroll.hasVerticalScroller = true
        listScroll.drawsBackground = false
        let listHolder = FlippedView()
        listHolder.addSubview(list)
        list.translatesAutoresizingMaskIntoConstraints = false
        listScroll.documentView = listHolder
        listHolder.translatesAutoresizingMaskIntoConstraints = false

        let left = NSView()
        let right = NSScrollView()
        right.hasVerticalScroller = true
        right.drawsBackground = true
        right.backgroundColor = .textBackgroundColor
        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = true
        textView.backgroundColor = .textBackgroundColor
        textView.textContainerInset = NSSize(width: 24, height: 20)
        textView.isVerticallyResizable = true
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = true
        right.documentView = textView

        let divider = NSBox()
        divider.boxType = .separator

        let content = NSView()
        for v in [left, divider, right] {
            v.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview(v)
        }
        for v in [search, listScroll] {
            v.translatesAutoresizingMaskIntoConstraints = false
            left.addSubview(v)
        }
        window.contentView = content

        NSLayoutConstraint.activate([
            left.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            left.topAnchor.constraint(equalTo: content.topAnchor),
            left.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            left.widthAnchor.constraint(equalToConstant: 230),
            divider.leadingAnchor.constraint(equalTo: left.trailingAnchor),
            divider.topAnchor.constraint(equalTo: content.topAnchor),
            divider.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            divider.widthAnchor.constraint(equalToConstant: 1),
            right.leadingAnchor.constraint(equalTo: divider.trailingAnchor),
            right.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            right.topAnchor.constraint(equalTo: content.topAnchor),
            right.bottomAnchor.constraint(equalTo: content.bottomAnchor),

            search.leadingAnchor.constraint(equalTo: left.leadingAnchor, constant: 12),
            search.trailingAnchor.constraint(equalTo: left.trailingAnchor, constant: -12),
            search.topAnchor.constraint(equalTo: left.topAnchor, constant: 12),
            listScroll.leadingAnchor.constraint(equalTo: left.leadingAnchor, constant: 8),
            listScroll.trailingAnchor.constraint(equalTo: left.trailingAnchor, constant: -8),
            listScroll.topAnchor.constraint(equalTo: search.bottomAnchor, constant: 10),
            listScroll.bottomAnchor.constraint(equalTo: left.bottomAnchor, constant: -8),

            listHolder.leadingAnchor.constraint(equalTo: listScroll.contentView.leadingAnchor),
            listHolder.trailingAnchor.constraint(equalTo: listScroll.contentView.trailingAnchor),
            listHolder.topAnchor.constraint(equalTo: listScroll.contentView.topAnchor),
            list.leadingAnchor.constraint(equalTo: listHolder.leadingAnchor),
            list.trailingAnchor.constraint(equalTo: listHolder.trailingAnchor),
            list.topAnchor.constraint(equalTo: listHolder.topAnchor),
            list.bottomAnchor.constraint(equalTo: listHolder.bottomAnchor),
        ])

        filter()
    }

    func windowWillClose(_ notification: Notification) {
        // Keep the controller (and window) for next time; nothing to clean up.
    }

    // MARK: topics

    private func select(title: String?) {
        if let t = title, let i = topics.firstIndex(where: { $0.title.caseInsensitiveCompare(t) == .orderedSame }) {
            if !shown.contains(i) {
                search.stringValue = ""
                filter()
            }
            show(index: i)
        } else if shown.isEmpty == false && !shown.contains(selected) {
            show(index: shown[0])
        }
    }

    @objc private func searchChanged() { filter() }

    private func filter() {
        let q = search.stringValue.trimmingCharacters(in: .whitespaces)
        shown = topics.indices.filter { q.isEmpty || topics[$0].plainText.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) != nil }
        for v in list.arrangedSubviews {
            list.removeArrangedSubview(v)
            v.removeFromSuperview()
        }
        for i in shown {
            let b = NSButton(title: topics[i].title, target: self, action: #selector(topicClicked(_:)))
            b.tag = i
            b.bezelStyle = .recessed
            b.setButtonType(.pushOnPushOff)
            b.showsBorderOnlyWhileMouseInside = true
            b.alignment = .left
            b.font = NSFont.systemFont(ofSize: 13)
            b.translatesAutoresizingMaskIntoConstraints = false
            list.addArrangedSubview(b)
            b.widthAnchor.constraint(equalToConstant: 206).isActive = true
            b.heightAnchor.constraint(equalToConstant: 28).isActive = true
        }
        if shown.isEmpty {
            textView.textStorage?.setAttributedString(NSAttributedString(
                string: "No topics match \"\(q)\".",
                attributes: [.font: NSFont.systemFont(ofSize: 14), .foregroundColor: NSColor.secondaryLabelColor]))
        } else {
            show(index: shown.contains(selected) ? selected : shown[0])
        }
    }

    @objc private func topicClicked(_ sender: NSButton) { show(index: sender.tag) }

    private func show(index: Int) {
        selected = index
        for case let b as NSButton in list.arrangedSubviews {
            b.state = b.tag == index ? .on : .off
            b.font = b.tag == index ? NSFont.boldSystemFont(ofSize: 13) : NSFont.systemFont(ofSize: 13)
        }
        textView.textStorage?.setAttributedString(render(topics[index]))
        textView.scrollToBeginningOfDocument(nil)
    }

    // MARK: rendering the help markup

    private func render(_ t: HelpTopic) -> NSAttributedString {
        let out = NSMutableAttributedString()
        let body = NSFont.systemFont(ofSize: 14)
        let bold = NSFont.boldSystemFont(ofSize: 14)
        let code = NSFont.monospacedSystemFont(ofSize: 13, weight: .regular)

        func style(indent: CGFloat = 0, head: CGFloat = 0, before: CGFloat = 0, after: CGFloat = 4) -> NSParagraphStyle {
            let p = NSMutableParagraphStyle()
            p.firstLineHeadIndent = indent
            p.headIndent = head
            p.paragraphSpacingBefore = before
            p.paragraphSpacing = after
            p.lineSpacing = 2
            p.tabStops = [NSTextTab(textAlignment: .left, location: head, options: [:])]
            return p
        }

        func add(_ text: String, _ font: NSFont, _ color: NSColor, _ para: NSParagraphStyle, background: NSColor? = nil) {
            var attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: para]
            if let bg = background { attrs[.backgroundColor] = bg }
            out.append(NSAttributedString(string: text, attributes: attrs))
        }

        // **bold** and `code`
        func inline(_ text: String, _ para: NSParagraphStyle, background: NSColor? = nil) {
            var isBold = false, isCode = false, chunk = ""
            func flush() {
                guard !chunk.isEmpty else { return }
                add(chunk, isCode ? code : (isBold ? bold : body),
                    isCode ? accent : .labelColor, para, background: background)
                chunk = ""
            }
            var i = text.startIndex
            while i < text.endIndex {
                let c = text[i]
                let next = text.index(after: i)
                if !isCode && c == "*" && next < text.endIndex && text[next] == "*" {
                    flush(); isBold.toggle(); i = text.index(after: next); continue
                }
                if c == "`" { flush(); isCode.toggle(); i = next; continue }
                chunk.append(c)
                i = next
            }
            flush()
        }

        add(t.title + "\n", NSFont.systemFont(ofSize: 24, weight: .bold), accent, style(after: 10))
        var gap = true, inSteps = false
        for line in t.lines {
            if line.isEmpty { gap = true; inSteps = false; continue }
            if line.hasPrefix("## ") {
                add(String(line.dropFirst(3)) + "\n", NSFont.systemFont(ofSize: 17, weight: .semibold), .labelColor,
                    style(before: gap ? 10 : 16, after: 6))
                gap = true
                inSteps = false
                continue
            }
            let before: CGFloat = gap ? 6 : 0
            gap = false
            if line.hasPrefix("- ") {
                let p = style(indent: 8, head: 26, before: before)
                add("•\t", bold, accent, p)
                inline(String(line.dropFirst(2)), p)
                add("\n", body, .labelColor, p)
                inSteps = false
            } else if let r = line.range(of: "^[0-9]+\\. ", options: .regularExpression) {
                let p = style(indent: 0, head: 26, before: before)
                add(String(line[r]).trimmingCharacters(in: .whitespaces) + "\t", bold, accent, p)
                inline(String(line[r.upperBound...]), p)
                add("\n", body, .labelColor, p)
                inSteps = true
            } else if line.hasPrefix("> ") {
                let tip = NSColor.systemOrange.withAlphaComponent(0.12)
                let p = style(indent: 10, head: 10, before: 10, after: 8)
                add("Tip  ", bold, accent, p, background: tip)
                inline(String(line.dropFirst(2)), p, background: tip)
                add("\n", body, .labelColor, p)
                inSteps = false
            } else {
                // A note right under a numbered step belongs to that step: indent it to line up.
                let p = inSteps ? style(indent: 26, head: 26) : style(before: before)
                inline(line, p)
                add("\n", body, .labelColor, p)
            }
        }
        return out
    }
}

/// A view with a top-left origin, so the topic list starts at the top of its scroll view.
final class FlippedView: NSView {
    override var isFlipped: Bool { return true }
}
