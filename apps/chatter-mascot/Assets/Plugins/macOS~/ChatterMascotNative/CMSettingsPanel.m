/*
 * パネル（#76）。NSPanel + NSStackView の汎用レンダラ。ファイル選択と確認ダイアログも持つ。
 *
 * ★★ このファイルに設定のキーもラベルも1つも書かないこと。 kind を見てビューを組み、
 *   操作されたら key と value を返すだけ。「記録」のようなボタンの文字まで JSON の
 *   strings で受け取るのは、そこだけ例外にすると必ず増えるため。
 *
 *   唯一の例外は修飾キーの記号（⌃⌥⇧⌘）。あれは設定の語彙ではなく物理キーの字形で、
 *   記録中に「いま何を押さえているか」を出すためだけに使う。
 *
 * ★★ nonactivatingPanel にしないこと。 LSUIElement のアプリはキーウィンドウを取りづらく、
 *   ショートカットの記録にはキー入力が要る。Show のときに
 *   [NSApp activateIgnoringOtherApps:YES] を呼ぶ（→ CMNative.h）。
 *
 * ★ UniWindowController の窓には触らない。 あちらは Unity のゲームウィンドウ1枚を
 *   透過・最前面・クリック透過で握っている。こちらは AppKit で別の窓を作るだけなので、
 *   あの設定には一切干渉しない。
 */
#import <Cocoa/Cocoa.h>
#import <Carbon/Carbon.h>
#import <objc/runtime.h>
#import "CMNative.h"

/* ── 状態 ───────────────────────────────────────────────────── */

/* 0 = 設定 / 1 = このアプリについて。増やすときはここだけ */
#define CM_PANEL_COUNT 2

static NSPanel *gPanels[CM_PANEL_COUNT];
static NSStackView *gStacks[CM_PANEL_COUNT];
static NSDictionary *gStrings = nil;

@class CMPanelTarget;
static CMPanelTarget *gTarget = nil;

/* ショートカットの記録中だけ立つ */
static id gKeyMonitor = nil;
static id gFlagsMonitor = nil;
static NSButton *gRecordButton = nil;
static NSTextField *gRecordField = nil;
static NSString *gRecordKey = nil;
static NSString *gRecordOriginal = nil;

static BOOL CMValidPanel(int panelId)
{
    return panelId >= 0 && panelId < CM_PANEL_COUNT;
}

static NSString *CMString(NSString *name, NSString *fallback)
{
    id value = gStrings[name];
    if ([value isKindOfClass:[NSString class]] && [(NSString *)value length] > 0) return (NSString *)value;
    return fallback;
}

static NSDictionary *CMParseJson(const char *json, NSString *what)
{
    if (json == NULL) return nil;
    NSData *data = [@(json) dataUsingEncoding:NSUTF8StringEncoding];
    if (data == nil) return nil;

    NSError *error = nil;
    id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:&error];
    if (![root isKindOfClass:[NSDictionary class]]) {
        CMEmitLog([[NSString stringWithFormat:@"%@ の JSON を読めませんでした: %@",
                    what, error.localizedDescription] UTF8String]);
        return nil;
    }
    return (NSDictionary *)root;
}

/* ── ショートカットの記録 ─────────────────────────────────────── */

/*
 * Cocoa の修飾フラグ → Carbon のマスク。
 *
 * ★ RegisterEventHotKey が要求するのは Carbon の方。 ここで変換しておけば、
 *   C# 側（HotKeySpec）は「登録に使う形」をそのまま受け取れる。
 * ★ 知っている4つ以外を落とすこと。 Caps Lock / Fn / 左右の区別が混ざったまま
 *   登録すると、同じ組み合わせを打っても一致しない。
 */
static unsigned int CMCarbonModifiers(NSEventModifierFlags flags)
{
    unsigned int mask = 0;
    if (flags & NSEventModifierFlagCommand) mask |= cmdKey;
    if (flags & NSEventModifierFlagShift)   mask |= shiftKey;
    if (flags & NSEventModifierFlagOption)  mask |= optionKey;
    if (flags & NSEventModifierFlagControl) mask |= controlKey;
    return mask;
}

/* 記録中に「いま押さえている修飾キー」を出す。★ 設定の語彙ではなくキーの字形 */
static NSString *CMModifierGlyphs(NSEventModifierFlags flags)
{
    NSMutableString *text = [NSMutableString string];
    if (flags & NSEventModifierFlagControl) [text appendString:@"⌃"];
    if (flags & NSEventModifierFlagOption)  [text appendString:@"⌥"];
    if (flags & NSEventModifierFlagShift)   [text appendString:@"⇧"];
    if (flags & NSEventModifierFlagCommand) [text appendString:@"⌘"];
    return text;
}

/*
 * 記録をやめる。
 *
 * ★★ shownText を渡せるようにしてあるのが要点。 成功したのに元の文字列へ戻すと、
 *   C# の再描画が届くまでの一瞬だけ**古いショートカットが見える**（実機で指摘された）。
 *   押されたキーをそのまま出したままにしておけば、再描画が同じ文字列で上書きするので
 *   ちらつかない。
 */
static void CMStopRecording(NSString *shownText)
{
    if (gKeyMonitor != nil) {
        [NSEvent removeMonitor:gKeyMonitor];
        gKeyMonitor = nil;
    }
    if (gFlagsMonitor != nil) {
        [NSEvent removeMonitor:gFlagsMonitor];
        gFlagsMonitor = nil;
    }
    if (gRecordButton != nil) {
        gRecordButton.title = CMString(@"record", @"Record");
        gRecordButton = nil;
    }
    if (gRecordField != nil) {
        gRecordField.stringValue = shownText != nil ? shownText
                                 : (gRecordOriginal != nil ? gRecordOriginal : @"");
        gRecordField = nil;
    }
    gRecordKey = nil;
    gRecordOriginal = nil;
}

static void CMStartRecording(NSButton *button, NSTextField *field, NSString *key)
{
    CMStopRecording(nil);

    gRecordButton = button;
    gRecordField = field;
    gRecordKey = key;
    gRecordOriginal = field.stringValue;

    button.title = CMString(@"cancel", @"Cancel");
    field.stringValue = CMString(@"recording", @"…");

    /*
     * ★ ローカルモニタにすること。 addGlobalMonitorForEvents はアクセシビリティ権限の
     *   ダイアログを出す（グローバルショートカットに Carbon を選んだのと同じ理由）。
     *   ローカルなら自分のアプリに配送されるイベントだけを見るので権限が要らない。
     *
     * ★ nil を返して飲み込むこと。 返さないと Unity 側にもキーが届く。
     */
    gFlagsMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskFlagsChanged
                                                          handler:^NSEvent *(NSEvent *event) {
        if (gRecordField == nil) return event;
        NSString *glyphs = CMModifierGlyphs(event.modifierFlags);
        gRecordField.stringValue = glyphs.length > 0 ? glyphs : CMString(@"recording", @"…");
        return event;
    }];

    gKeyMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskKeyDown
                                                        handler:^NSEvent *(NSEvent *event) {
        if (gRecordKey == nil) return event;

        unsigned int mask = CMCarbonModifiers(event.modifierFlags);

        /*
         * ★ 修飾キー無しの esc を「中止」にすること。 記録を始めてしまったユーザーが
         *   抜ける手段が無いと、パネルのどこを押しても記録が続く。
         *   ★ 修飾キー付きの esc は記録する（⌃⌥esc は正当なショートカット）。
         */
        if (event.keyCode == kVK_Escape && mask == 0) {
            CMStopRecording(nil);
            return nil;
        }

        NSString *key = gRecordKey;
        NSString *value = [NSString stringWithFormat:@"%u,%u", (unsigned int)event.keyCode, mask];

        /*
         * 押されたキーをそのまま出したままにする（→ CMStopRecording の ★★）。
         * ★ 記号を組むのに使うのは「修飾キーの字形」と「キーの刻印」だけで、
         *   設定の語彙ではない。保存される文字列（ctrl+opt+m）は C# が作る。
         */
        NSString *letter = event.charactersIgnoringModifiers.uppercaseString;
        NSString *shown = [CMModifierGlyphs(event.modifierFlags)
                           stringByAppendingString:letter != nil ? letter : @""];

        /*
         * ★ 先に記録を止めること。 CMEmitSetting の先（C# の drain）で
         *   パネルが組み直されると、ここで触っているビューが消える。
         */
        CMStopRecording(shown);
        CMEmitSetting([key UTF8String], [value UTF8String]);
        return nil;
    }];
}

/* ── 操作の受け口 ───────────────────────────────────────────── */

@interface CMPanelTarget : NSObject <NSWindowDelegate, NSOpenSavePanelDelegate>
@property (nonatomic, strong) NSArray<NSString *> *allowedExtensions;
- (void)onCheckbox:(id)sender;
- (void)onSlider:(id)sender;
- (void)onPopUp:(id)sender;
- (void)onButton:(id)sender;
- (void)onRecord:(id)sender;
@end

@implementation CMPanelTarget

- (void)onCheckbox:(id)sender
{
    NSButton *button = (NSButton *)sender;
    id key = button.identifier;
    if (![key isKindOfClass:[NSString class]]) return;
    CMEmitSetting([(NSString *)key UTF8String], button.state == NSControlStateValueOn ? "true" : "false");
}

- (void)onSlider:(id)sender
{
    NSSlider *slider = (NSSlider *)sender;
    id key = slider.identifier;
    if (![key isKindOfClass:[NSString class]]) return;

    /* 値の表示は常に更新する（つまみを掴んでいる間も追従させる） */
    NSTextField *readout = (NSTextField *)objc_getAssociatedObject(slider, "cm.readout");
    /*
     * ★ %g は C ロケール。 %@ で NSNumber を出すとロケールによって "0,7" になり、
     *   C# 側のパースが落ちる（そのキーだけ既定に戻る、という気づきにくい形で出る）。
     */
    NSString *text = [NSString stringWithFormat:@"%g", slider.doubleValue];
    if ([readout isKindOfClass:[NSTextField class]]) readout.stringValue = text;

    /*
     * ★ ドラッグ中は投げないこと。 1操作で数十回の PATCH / 保存が走る。
     *   離した瞬間（mouseUp）と、矢印キーでの調整は投げる。
     * ★ これは最後の砦で、本命の間引きは C# 側のデバウンス。
     */
    NSEvent *current = [NSApp currentEvent];
    if (current != nil && current.type == NSEventTypeLeftMouseDragged) return;

    CMEmitSetting([(NSString *)key UTF8String], [text UTF8String]);
}

- (void)onPopUp:(id)sender
{
    NSPopUpButton *popUp = (NSPopUpButton *)sender;
    id key = popUp.identifier;
    if (![key isKindOfClass:[NSString class]]) return;

    id value = popUp.selectedItem.representedObject;
    if (![value isKindOfClass:[NSString class]]) return;
    CMEmitSetting([(NSString *)key UTF8String], [(NSString *)value UTF8String]);
}

- (void)onButton:(id)sender
{
    NSButton *button = (NSButton *)sender;
    id key = button.identifier;
    if (![key isKindOfClass:[NSString class]]) return;
    CMEmitSetting([(NSString *)key UTF8String], "");
}

- (void)onRecord:(id)sender
{
    NSButton *button = (NSButton *)sender;
    id key = button.identifier;
    if (![key isKindOfClass:[NSString class]]) return;

    /* 記録中にもう一度押したら中止 */
    if (gRecordButton == button) {
        CMStopRecording(nil);
        return;
    }

    NSTextField *field = (NSTextField *)objc_getAssociatedObject(button, "cm.field");
    if (![field isKindOfClass:[NSTextField class]]) return;
    CMStartRecording(button, field, (NSString *)key);
}

/*
 * ★ 閉じたら記録を止めること。モニタを付けたままにすると、パネルが無いのにキーを飲み込む
 *
 * ★★ 閉じたことを C# へ返すこと（#76）。 赤いボタンで閉じる経路は **ここでしか捕まらない**
 *   （CM_PanelHide は自分で閉じる経路で、orderOut: は この通知を出さないので二重にはならない）。
 *   返さないと C# 側の「開いている」が真のまま残り、開き直したときに
 *   **数分前の注記が、いま起きたことのように出る**。
 * ★ どのパネルかは C# が決める（id を返すだけで、設定 / について の区別はこちらに書かない）。
 */
- (void)windowWillClose:(NSNotification *)notification
{
    CMStopRecording(nil);

    id closed = notification.object;
    for (int i = 0; i < CM_PANEL_COUNT; i++) {
        if (gPanels[i] != nil && gPanels[i] == closed) {
            CMEmitPanel(i, "closed");
            return;
        }
    }
}

/*
 * ★★ allowedContentTypes で絞らないための delegate。 .vrm のように
 *   システムに登録された UTI を持たない拡張子は、UTType 経由だと dynamic UTI になり
 *   **拡張子が一致してもグレーアウトする**（UniWindowController の FilePanel がこれ）。
 *   拡張子を自分で見れば UTI の登録状況に依存しない。
 */
- (BOOL)panel:(id)sender shouldEnableURL:(NSURL *)url
{
    if (url == nil) return NO;
    if (self.allowedExtensions.count == 0) return YES;

    NSNumber *isDirectory = nil;
    /* ★ ディレクトリは必ず通すこと。通さないと中に入れない */
    if ([url getResourceValue:&isDirectory forKey:NSURLIsDirectoryKey error:NULL]
        && isDirectory.boolValue) {
        /* ★ .app のようなパッケージは「入れるディレクトリ」ではないので拡張子で見る */
        NSNumber *isPackage = nil;
        if ([url getResourceValue:&isPackage forKey:NSURLIsPackageKey error:NULL]
            && isPackage.boolValue) {
            /* パッケージは拡張子判定へ落とす */
        } else {
            return YES;
        }
    }

    NSString *ext = url.pathExtension.lowercaseString;
    if (ext.length == 0) return NO;
    for (NSString *allowed in self.allowedExtensions) {
        if ([allowed isKindOfClass:[NSString class]] && [ext isEqualToString:allowed.lowercaseString]) return YES;
    }
    return NO;
}

@end

/* ── ビューの組み立て ───────────────────────────────────────── */

/*
 * ★ 折り返す複数行ラベルには preferredMaxLayoutWidth が要る。 無いと Auto Layout は
 *   「1行ぶんの幅」を要求し続け、長い note でウィンドウごと横に伸びる（実機で踏んだ）。
 *   幅そのものは呼び出し側の widthAnchor が決めるので、ここは折り返しの基準だけ与える。
 */
static const CGFloat CMWrapWidth = 440.0;

/*
 * 幅いっぱいに置くラベル。折り返しの基準を**自分の幅**に合わせ直す。
 *
 * ★ 固定値（CMWrapWidth）のままだと、パネルを広げても折り返し位置が動かない。
 *   ライセンス本文のように**元から整形済み**の文章では、二重の折り返しになって
 *   1文字だけの行が出る（実機で踏んだ）。
 *
 * ★ 変わったときだけ書くこと。 毎回書くと invalidateIntrinsicContentSize が
 *   レイアウトを呼び戻して振動する。
 */
@interface CMWrappingLabel : NSTextField
@end

@implementation CMWrappingLabel

- (void)layout
{
    CGFloat w = NSWidth(self.frame);
    if (w > 1.0 && fabs(self.preferredMaxLayoutWidth - w) > 0.5) {
        self.preferredMaxLayoutWidth = w;
        [self invalidateIntrinsicContentSize];
    }
    [super layout];
}

@end

static NSTextField *CMMakeLabel(Class cls, NSString *text, BOOL bold, BOOL secondary)
{
    NSTextField *label = [[cls alloc] initWithFrame:NSZeroRect];
    label.stringValue = text != nil ? text : @"";
    label.editable = NO;
    label.bordered = NO;
    label.bezeled = NO;
    label.drawsBackground = NO;
    label.lineBreakMode = NSLineBreakByWordWrapping;
    label.selectable = YES;
    label.preferredMaxLayoutWidth = CMWrapWidth;
    label.font = [NSFont systemFontOfSize:[NSFont systemFontSize]];
    label.textColor = [NSColor labelColor];
    if (bold) label.font = [NSFont boldSystemFontOfSize:[NSFont systemFontSize]];
    if (secondary) {
        label.font = [NSFont systemFontOfSize:[NSFont smallSystemFontSize]];
        label.textColor = [NSColor secondaryLabelColor];
    }
    return label;
}

/* 行の中に置くラベル（項目名・数値・記号）。幅は中身で決まる */
static NSTextField *CMLabel(NSString *text, BOOL bold, BOOL secondary)
{
    return CMMakeLabel([NSTextField class], text, bold, secondary);
}

/* 幅いっぱいに置くラベル（見出し・note・本文）。折り返しがパネル幅に追従する */
static NSTextField *CMFlowLabel(NSString *text, BOOL bold, BOOL secondary)
{
    return CMMakeLabel([CMWrappingLabel class], text, bold, secondary);
}

static NSStackView *CMRow(NSView *left, NSView *right)
{
    NSStackView *row = [NSStackView stackViewWithViews:right != nil ? @[left, right] : @[left]];
    row.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    row.alignment = NSLayoutAttributeCenterY;
    row.spacing = 12;
    row.distribution = NSStackViewDistributionFill;

    /*
     * ★ 伸びるのはコントロール側、縮まないのはラベル側。
     *   逆にすると、ラベルが「音声スタイ…」のように切れてコントロールが余る。
     */
    [left setContentHuggingPriority:NSLayoutPriorityRequired
                     forOrientation:NSLayoutConstraintOrientationHorizontal];
    [left setContentCompressionResistancePriority:NSLayoutPriorityRequired
                                   forOrientation:NSLayoutConstraintOrientationHorizontal];
    if (right != nil) {
        [right setContentHuggingPriority:NSLayoutPriorityDefaultLow
                          forOrientation:NSLayoutConstraintOrientationHorizontal];
    }
    return row;
}

static NSView *CMBuildItem(NSDictionary *item)
{
    id kind = item[@"kind"];
    id label = item[@"label"];
    if (![kind isKindOfClass:[NSString class]]) return nil;
    if (![label isKindOfClass:[NSString class]]) return nil;

    NSString *k = (NSString *)kind;
    NSString *text = (NSString *)label;

    if ([k isEqualToString:@"section"]) return CMFlowLabel(text, YES, NO);

    id keyValue = item[@"key"];
    if (![keyValue isKindOfClass:[NSString class]]) return nil;
    NSString *key = (NSString *)keyValue;

    /* enabled が無ければ有効。C# 側は必ず入れてくるが、古い版と組み合わさることがある */
    id enabledValue = item[@"enabled"];
    BOOL enabled = (enabledValue == nil) ? YES : [enabledValue boolValue];

    NSView *control = nil;

    if ([k isEqualToString:@"bool"]) {
        NSButton *check = [NSButton checkboxWithTitle:text target:gTarget action:@selector(onCheckbox:)];
        check.identifier = key;
        check.state = [item[@"value"] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;
        check.enabled = enabled;
        control = check;  /* ラベルはチェックボックス自身が持つ */
    } else if ([k isEqualToString:@"button"]) {
        NSButton *button = [NSButton buttonWithTitle:text target:gTarget action:@selector(onButton:)];
        button.identifier = key;
        button.enabled = enabled;
        control = button;
    } else if ([k isEqualToString:@"slider"]) {
        NSSlider *slider = [NSSlider sliderWithTarget:gTarget action:@selector(onSlider:)];
        slider.identifier = key;
        slider.minValue = [item[@"min"] doubleValue];
        slider.maxValue = [item[@"max"] doubleValue];
        slider.doubleValue = [item[@"value"] doubleValue];
        slider.enabled = enabled;
        slider.continuous = YES;

        /*
         * ★ 刻みは NSSlider に任せること。 setNumberOfTickMarks: と
         *   setAllowsTickMarkValuesOnly: で吸着させれば、ネイティブ側に
         *   「その項目の刻みがいくつか」という知識を持たせずに済む。
         */
        double step = [item[@"step"] doubleValue];
        if (step > 0) {
            NSInteger ticks = (NSInteger)llround((slider.maxValue - slider.minValue) / step) + 1;
            if (ticks > 1 && ticks <= 64) {
                slider.numberOfTickMarks = ticks;
                slider.allowsTickMarkValuesOnly = YES;
                slider.tickMarkPosition = NSTickMarkPositionBelow;
            }
        }

        NSTextField *readout = CMLabel([NSString stringWithFormat:@"%g", slider.doubleValue], NO, NO);
        [readout.widthAnchor constraintEqualToConstant:44].active = YES;
        readout.alignment = NSTextAlignmentRight;
        objc_setAssociatedObject(slider, "cm.readout", readout, OBJC_ASSOCIATION_RETAIN_NONATOMIC);

        NSStackView *pair = [NSStackView stackViewWithViews:@[slider, readout]];
        pair.orientation = NSUserInterfaceLayoutOrientationHorizontal;
        pair.spacing = 8;
        [slider.widthAnchor constraintGreaterThanOrEqualToConstant:200].active = YES;
        control = pair;
    } else if ([k isEqualToString:@"choice"]) {
        NSPopUpButton *popUp = [[NSPopUpButton alloc] initWithFrame:NSZeroRect pullsDown:NO];
        popUp.identifier = key;
        popUp.target = gTarget;
        popUp.action = @selector(onPopUp:);

        id choices = item[@"choices"];
        NSString *selected = [item[@"value"] isKindOfClass:[NSString class]] ? item[@"value"] : @"";
        NSInteger count = 0;
        if ([choices isKindOfClass:[NSArray class]]) {
            for (id raw in (NSArray *)choices) {
                if (![raw isKindOfClass:[NSDictionary class]]) continue;
                NSDictionary *choice = (NSDictionary *)raw;
                id value = choice[@"value"];
                id title = choice[@"label"];
                if (![value isKindOfClass:[NSString class]]) continue;

                /*
                 * ★★ addItemWithTitle: を使わないこと。 同じタイトルの項目が既にあると
                 *   **古い方を取り除いてから**追加する（NSPopUpButton の文書化された挙動）。
                 *   話者のラベル（名前（スタイル名））が衝突すると、先に入れた話者が
                 *   メニューから外れて **パネルから選べなくなる**のに count は増えるので、
                 *   count == 0 の「—」フォールバックにも落ちない。しかも現在の ttsSpeakerId が
                 *   外れた方だと、selectItem: は切り離された項目に対して行われ、
                 *   **別の話者が選択中であるかのように見える**。自分で NSMenuItem を作れば重複を許容できる。
                 */
                NSString *label = [title isKindOfClass:[NSString class]] ? (NSString *)title : (NSString *)value;
                NSMenuItem *entry = [[NSMenuItem alloc] initWithTitle:label action:NULL keyEquivalent:@""];
                entry.representedObject = value;
                [popUp.menu addItem:entry];
                if ([(NSString *)value isEqualToString:selected]) [popUp selectItem:entry];
                count++;
            }
        }
        if (count == 0) {
            /* ★ 項目ごと消さないこと。 消すと「設定が無い」に見える（→ C# 側の note） */
            [popUp addItemWithTitle:CMString(@"empty", @"—")];
            popUp.enabled = NO;
        } else {
            popUp.enabled = enabled;
        }
        [popUp.widthAnchor constraintGreaterThanOrEqualToConstant:240].active = YES;
        control = popUp;
    } else if ([k isEqualToString:@"hotkey"]) {
        NSString *shown = [item[@"value"] isKindOfClass:[NSString class]] ? item[@"value"] : @"";
        NSTextField *field = CMLabel(shown, NO, NO);
        field.alignment = NSTextAlignmentCenter;
        field.drawsBackground = YES;
        field.backgroundColor = [NSColor textBackgroundColor];
        field.bezeled = YES;
        field.bezelStyle = NSTextFieldRoundedBezel;
        [field.widthAnchor constraintEqualToConstant:96].active = YES;

        NSButton *record = [NSButton buttonWithTitle:CMString(@"record", @"Record")
                                              target:gTarget
                                              action:@selector(onRecord:)];
        record.identifier = key;
        record.enabled = enabled;
        objc_setAssociatedObject(record, "cm.field", field, OBJC_ASSOCIATION_RETAIN_NONATOMIC);

        NSStackView *pair = [NSStackView stackViewWithViews:@[field, record]];
        pair.orientation = NSUserInterfaceLayoutOrientationHorizontal;
        pair.spacing = 8;
        control = pair;
    } else if ([k isEqualToString:@"text"]) {
        NSString *value = [item[@"value"] isKindOfClass:[NSString class]] ? item[@"value"] : @"";
        NSTextField *body = CMFlowLabel(value, NO, NO);
        body.font = [NSFont userFixedPitchFontOfSize:[NSFont smallSystemFontSize]];
        control = body;
    } else {
        /* ★ 知らない kind は「壊れている」ではない。新しい C# と古いバンドルの組み合わせ */
        return nil;
    }

    NSMutableArray<NSView *> *rows = [NSMutableArray array];
    if ([k isEqualToString:@"bool"] || [k isEqualToString:@"button"]) {
        [rows addObject:control];
    } else if ([k isEqualToString:@"text"]) {
        [rows addObject:CMFlowLabel(text, NO, YES)];
        [rows addObject:control];
    } else {
        [rows addObject:CMRow(CMLabel(text, NO, NO), control)];
    }

    id note = item[@"note"];
    if ([note isKindOfClass:[NSString class]] && [(NSString *)note length] > 0) {
        [rows addObject:CMFlowLabel((NSString *)note, NO, YES)];
    }

    if (rows.count == 1) return rows[0];

    NSStackView *group = [NSStackView stackViewWithViews:rows];
    group.orientation = NSUserInterfaceLayoutOrientationVertical;
    group.alignment = NSLayoutAttributeLeading;
    group.spacing = 2;

    /*
     * ★★ 子の横幅を明示すること。 縦の NSStackView は alignment=leading だと
     *   子を「左揃えにするだけ」で幅は内容依存になる。付けないと、
     *   **note を持つ行だけスライダーが縮む**（note の無い行と幅が揃わない）。
     */
    for (NSView *row in rows) {
        [row.widthAnchor constraintEqualToAnchor:group.widthAnchor].active = YES;
    }
    return group;
}

static void CMApplySchema(int panelId, NSDictionary *root)
{
    NSStackView *stack = gStacks[panelId];
    if (stack == nil) return;

    /* ★ 記録中に組み直すとモニタが宙に浮く。先に止める */
    CMStopRecording(nil);

    id strings = root[@"strings"];
    gStrings = [strings isKindOfClass:[NSDictionary class]] ? (NSDictionary *)strings : nil;

    for (NSView *view in [stack.views copy]) [stack removeView:view];

    id items = root[@"items"];
    if ([items isKindOfClass:[NSArray class]]) {
        for (id raw in (NSArray *)items) {
            if (![raw isKindOfClass:[NSDictionary class]]) continue;
            NSView *view = CMBuildItem((NSDictionary *)raw);
            if (view == nil) continue;
            [stack addView:view inGravity:NSStackViewGravityTop];
            /* ★ 外側でも同じ（→ CMBuildItem の ★★）。edgeInsets のぶんを引く */
            [view.widthAnchor
                constraintEqualToAnchor:stack.widthAnchor
                               constant:-(stack.edgeInsets.left + stack.edgeInsets.right)].active = YES;
        }
    }

    id title = root[@"title"];
    if (gPanels[panelId] != nil && [title isKindOfClass:[NSString class]]) {
        gPanels[panelId].title = (NSString *)title;
    }
}

static void CMEnsurePanel(int panelId)
{
    if (gPanels[panelId] != nil) return;
    if (gTarget == nil) gTarget = [[CMPanelTarget alloc] init];

    /*
     * ★ 「について」は広めに出すこと。 ライセンス本文は元から 80 桁前後で整形されていて、
     *   設定と同じ 520pt だと折り返しが二重になる。
     */
    NSRect frame = panelId == 1 ? NSMakeRect(0, 0, 680, 640) : NSMakeRect(0, 0, 520, 640);
    /*
     * ★ nonactivatingPanel を入れないこと（→ ファイル冒頭）。
     * ★ utilityWindow にしておくと、他のアプリを前に出したときに一緒に引っ込む
     *   —— 常駐マスコットの設定としてはそちらが素直。
     */
    NSPanel *panel = [[NSPanel alloc] initWithContentRect:frame
                                               styleMask:(NSWindowStyleMaskTitled |
                                                          NSWindowStyleMaskClosable |
                                                          NSWindowStyleMaskResizable |
                                                          NSWindowStyleMaskUtilityWindow)
                                                 backing:NSBackingStoreBuffered
                                                   defer:YES];
    panel.releasedWhenClosed = NO;   /* ★ 閉じても捨てない。次に開くときに作り直さない */
    panel.hidesOnDeactivate = NO;
    panel.delegate = gTarget;

    /*
     * ★★ setFrameAutosaveName: だけでは復元されない。 保存はされるが、読み戻すのは
     *   setFrameUsingName:。付け忘れると、毎回 initWithContentRect の矩形
     *   （= AppKit は bottom-up なので**画面の左下**）に出る。
     */
    panel.frameAutosaveName = [NSString stringWithFormat:@"ChatterMascotPanel%d", panelId];
    if (![panel setFrameUsingName:panel.frameAutosaveName]) [panel center];

    NSStackView *stack = [[NSStackView alloc] init];
    stack.orientation = NSUserInterfaceLayoutOrientationVertical;
    stack.alignment = NSLayoutAttributeLeading;
    stack.spacing = 10;
    stack.edgeInsets = NSEdgeInsetsMake(16, 16, 16, 16);
    stack.translatesAutoresizingMaskIntoConstraints = NO;

    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:frame];
    scroll.hasVerticalScroller = YES;
    scroll.autohidesScrollers = YES;
    scroll.drawsBackground = NO;
    scroll.translatesAutoresizingMaskIntoConstraints = NO;
    scroll.documentView = stack;

    NSView *content = panel.contentView;
    [content addSubview:scroll];
    [NSLayoutConstraint activateConstraints:@[
        [scroll.leadingAnchor constraintEqualToAnchor:content.leadingAnchor],
        [scroll.trailingAnchor constraintEqualToAnchor:content.trailingAnchor],
        [scroll.topAnchor constraintEqualToAnchor:content.topAnchor],
        [scroll.bottomAnchor constraintEqualToAnchor:content.bottomAnchor],
        /* ★ 横幅をクリップ領域に合わせること。合わせないと折り返しが効かず横スクロールになる */
        [stack.widthAnchor constraintEqualToAnchor:scroll.contentView.widthAnchor],
        [stack.topAnchor constraintEqualToAnchor:scroll.contentView.topAnchor],
        [stack.leadingAnchor constraintEqualToAnchor:scroll.contentView.leadingAnchor],
    ]];

    gPanels[panelId] = panel;
    gStacks[panelId] = stack;
}

/* ── C# から呼ぶ口 ─────────────────────────────────────────── */

bool CM_PanelShow(int panelId, const char* schemaJson)
{
    /* ★ 非メインからは成功を騙らない（→ CMNative.h の契約） */
    if (!CMIsMainThread()) {
        CMEmitLog("CM_PanelShow をメインスレッド以外から呼びました");
        return false;
    }
    if (!CMValidPanel(panelId)) return false;

    NSDictionary *root = CMParseJson(schemaJson, @"パネル");
    if (root == nil) return false;

    CMRunOnMain(^{
        CMEnsurePanel(panelId);
        CMApplySchema(panelId, root);

        /*
         * ★★ activateIgnoringOtherApps を忘れないこと。 LSUIElement のアプリは
         *   既定でアクティブになれないので、これが無いと
         *   「スライダーは動くがショートカットの記録が始まらない」という形で出る。
         */
        [NSApp activateIgnoringOtherApps:YES];
        [gPanels[panelId] makeKeyAndOrderFront:nil];

        /*
         * ★ 出せたかどうかを1行残すこと。 「開かない」は
         *   （a）Show が失敗した（b）画面外に出た（c）他のウィンドウの背後に居る
         *   のどれかで、手当てが全部違う。実際に (c) を踏んだ。
         */
        NSPanel *panel = gPanels[panelId];
        CMEmitLog([[NSString stringWithFormat:
            @"パネル%d: frame=%.0f,%.0f %.0fx%.0f visible=%d key=%d screen=%@",
            panelId,
            panel.frame.origin.x, panel.frame.origin.y,
            panel.frame.size.width, panel.frame.size.height,
            (int)panel.isVisible, (int)panel.isKeyWindow,
            panel.screen == nil ? @"なし" : NSStringFromRect(panel.screen.frame)] UTF8String]);
    });
    return true;
}

bool CM_PanelUpdate(int panelId, const char* schemaJson)
{
    if (!CMIsMainThread()) {
        CMEmitLog("CM_PanelUpdate をメインスレッド以外から呼びました");
        return false;
    }
    if (!CMValidPanel(panelId)) return false;
    if (gPanels[panelId] == nil || !gPanels[panelId].isVisible) return false;

    NSDictionary *root = CMParseJson(schemaJson, @"パネル");
    if (root == nil) return false;

    CMRunOnMain(^{ CMApplySchema(panelId, root); });
    return true;
}

void CM_PanelHide(int panelId)
{
    if (!CMValidPanel(panelId)) return;
    CMRunOnMain(^{
        CMStopRecording(nil);
        if (gPanels[panelId] != nil) [gPanels[panelId] orderOut:nil];
    });
}

bool CM_PanelIsVisible(int panelId)
{
    if (!CMIsMainThread()) return false;
    if (!CMValidPanel(panelId)) return false;
    return gPanels[panelId] != nil && gPanels[panelId].isVisible;
}

bool CM_OpenFilePanel(const char* optionsJson)
{
    if (!CMIsMainThread()) {
        CMEmitLog("CM_OpenFilePanel をメインスレッド以外から呼びました");
        return false;
    }

    NSDictionary *root = CMParseJson(optionsJson, @"ファイル選択");
    if (root == nil) return false;

    id key = root[@"key"];
    if (![key isKindOfClass:[NSString class]]) return false;

    if (gTarget == nil) gTarget = [[CMPanelTarget alloc] init];

    NSMutableArray<NSString *> *extensions = [NSMutableArray array];
    id list = root[@"extensions"];
    if ([list isKindOfClass:[NSArray class]]) {
        for (id ext in (NSArray *)list) {
            if ([ext isKindOfClass:[NSString class]] && [(NSString *)ext length] > 0) [extensions addObject:ext];
        }
    }
    gTarget.allowedExtensions = extensions;

    NSOpenPanel *panel = [NSOpenPanel openPanel];
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = NO;
    panel.allowsMultipleSelection = NO;
    panel.resolvesAliases = YES;
    /*
     * ★★ allowedContentTypes を設定しないこと（→ CMNative.h）。
     *   絞り込みは delegate の panel:shouldEnableURL: が行う。
     */
    panel.delegate = gTarget;

    id title = root[@"title"];
    if ([title isKindOfClass:[NSString class]]) panel.title = (NSString *)title;
    id message = root[@"message"];
    if ([message isKindOfClass:[NSString class]]) panel.message = (NSString *)message;
    id button = root[@"button"];
    if ([button isKindOfClass:[NSString class]]) panel.prompt = (NSString *)button;

    /* ★ キーウィンドウを取れないと、パネルが背後に出て「押しても何も起きない」に見える */
    [NSApp activateIgnoringOtherApps:YES];

    NSModalResponse response = [panel runModal];
    gTarget.allowedExtensions = nil;
    if (response != NSModalResponseOK) return false;   /* ★ 取り消しは何も投げない */

    NSURL *url = panel.URLs.firstObject;
    if (url == nil) return false;

    CMEmitSetting([(NSString *)key UTF8String], [url.path UTF8String]);
    return true;
}

bool CM_Confirm(const char* optionsJson)
{
    if (!CMIsMainThread()) {
        CMEmitLog("CM_Confirm をメインスレッド以外から呼びました");
        return false;
    }

    NSDictionary *root = CMParseJson(optionsJson, @"確認");
    if (root == nil) return false;

    NSAlert *alert = [[NSAlert alloc] init];
    id title = root[@"title"];
    if ([title isKindOfClass:[NSString class]]) alert.messageText = (NSString *)title;
    id message = root[@"message"];
    if ([message isKindOfClass:[NSString class]]) alert.informativeText = (NSString *)message;

    id ok = root[@"ok"];
    NSButton *okButton = [alert addButtonWithTitle:[ok isKindOfClass:[NSString class]] ? ok : @"OK"];
    id cancel = root[@"cancel"];
    [alert addButtonWithTitle:[cancel isKindOfClass:[NSString class]] ? cancel : @"Cancel"];

    /* ★ 取り消せない操作は赤くすること。押す前に一度手が止まる */
    if ([root[@"destructive"] boolValue]) {
        alert.alertStyle = NSAlertStyleCritical;
        if (@available(macOS 11.0, *)) okButton.hasDestructiveAction = YES;
    }

    [NSApp activateIgnoringOtherApps:YES];
    return [alert runModal] == NSAlertFirstButtonReturn;
}
