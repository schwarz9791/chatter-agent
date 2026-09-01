/*
 * 設定パネル（#76）。NSPanel + NSStackView の汎用レンダラ。
 *
 * ★★ このファイルに設定のキーもラベルも1つも書かないこと。 kind を見てビューを組み、
 *   操作されたら key と value を返すだけ。「記録」のようなボタンの文字まで JSON の
 *   strings で受け取っているのは、そこだけ例外にすると必ず増えるため
 *   （CMStatusItem.m がメニューに対してやっているのと同じ規律）。
 *
 *   唯一の例外は修飾キーの記号（⌃⌥⇧⌘）。あれは設定の語彙ではなく
 *   物理キーの字形で、記録中に「いま何を押さえているか」を出すためだけに使う。
 *
 * ★★ nonactivatingPanel にしないこと。 LSUIElement のアプリはキーウィンドウを取りづらく、
 *   ショートカットの記録にはキー入力が要る。Show のときに
 *   [NSApp activateIgnoringOtherApps:YES] を呼ぶ（→ CMNative.h）。
 *
 * ★ UniWindowController の窓には触らない。 あちらは Unity のゲームウィンドウ1枚を
 *   透過・最前面・クリック透過で握っている。こちらは AppKit で別の窓を作るだけなので、
 *   あの設定には一切干渉しない（複数ウィンドウ非対応というのは
 *   「Unity のゲームウィンドウが1枚しか作れない」という話）。
 */
#import <Cocoa/Cocoa.h>
#import <Carbon/Carbon.h>
#import <objc/runtime.h>
#import "CMNative.h"

/* ── 状態 ───────────────────────────────────────────────────── */

static NSPanel *gPanel = nil;
static NSStackView *gStack = nil;
static NSDictionary *gStrings = nil;

@class CMSettingsTarget;
static CMSettingsTarget *gTarget = nil;

/* ショートカットの記録中だけ立つ */
static id gKeyMonitor = nil;
static id gFlagsMonitor = nil;
static NSButton *gRecordButton = nil;
static NSTextField *gRecordField = nil;
static NSString *gRecordKey = nil;
static NSString *gRecordOriginal = nil;

static NSString *CMString(NSString *name, NSString *fallback)
{
    id value = gStrings[name];
    if ([value isKindOfClass:[NSString class]] && [(NSString *)value length] > 0) return (NSString *)value;
    return fallback;
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

static void CMStopRecording(void)
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
        gRecordField.stringValue = gRecordOriginal != nil ? gRecordOriginal : @"";
        gRecordField = nil;
    }
    gRecordKey = nil;
    gRecordOriginal = nil;
}

static void CMStartRecording(NSButton *button, NSTextField *field, NSString *key)
{
    CMStopRecording();

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
            CMStopRecording();
            return nil;
        }

        NSString *key = gRecordKey;
        NSString *value = [NSString stringWithFormat:@"%u,%u", (unsigned int)event.keyCode, mask];
        /*
         * ★ 先に記録を止めること。 CMEmitSetting の先（C# の drain）で
         *   パネルが組み直されると、ここで触っているビューが消える。
         */
        CMStopRecording();
        CMEmitSetting([key UTF8String], [value UTF8String]);
        return nil;
    }];
}

/* ── 操作の受け口 ───────────────────────────────────────────── */

@interface CMSettingsTarget : NSObject <NSWindowDelegate>
- (void)onCheckbox:(id)sender;
- (void)onSlider:(id)sender;
- (void)onPopUp:(id)sender;
- (void)onButton:(id)sender;
- (void)onRecord:(id)sender;
@end

@implementation CMSettingsTarget

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
     *   ★ これは最後の砦で、本命の間引きは C# 側のデバウンス。
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
        CMStopRecording();
        return;
    }

    NSTextField *field = (NSTextField *)objc_getAssociatedObject(button, "cm.field");
    if (![field isKindOfClass:[NSTextField class]]) return;
    CMStartRecording(button, field, (NSString *)key);
}

/* ★ 閉じたら記録を止めること。モニタを付けたままにすると、パネルが無いのにキーを飲み込む */
- (void)windowWillClose:(NSNotification *)notification
{
    CMStopRecording();
}

@end

/* ── ビューの組み立て ───────────────────────────────────────── */

static NSTextField *CMLabel(NSString *text, BOOL bold, BOOL secondary)
{
    NSTextField *label = [NSTextField labelWithString:text != nil ? text : @""];
    label.lineBreakMode = NSLineBreakByWordWrapping;
    label.selectable = YES;
    if (bold) label.font = [NSFont boldSystemFontOfSize:[NSFont systemFontSize]];
    if (secondary) {
        label.font = [NSFont systemFontOfSize:[NSFont smallSystemFontSize]];
        label.textColor = [NSColor secondaryLabelColor];
    }
    return label;
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

    if ([k isEqualToString:@"section"]) return CMLabel(text, YES, NO);

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
                [popUp addItemWithTitle:[title isKindOfClass:[NSString class]] ? title : value];
                NSMenuItem *entry = popUp.lastItem;
                entry.representedObject = value;
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
        NSTextField *body = CMLabel(value, NO, NO);
        body.font = [NSFont userFixedPitchFontOfSize:[NSFont smallSystemFontSize]];
        /* ★ 折り返しの基準を持たせること。無いと1行の長大なラベルになって横に溢れる */
        body.preferredMaxLayoutWidth = 420;
        control = body;
    } else {
        /* ★ 知らない kind は「壊れている」ではない。新しい C# と古いバンドルの組み合わせ */
        return nil;
    }

    NSMutableArray<NSView *> *rows = [NSMutableArray array];
    if ([k isEqualToString:@"bool"] || [k isEqualToString:@"button"]) {
        [rows addObject:control];
    } else if ([k isEqualToString:@"text"]) {
        [rows addObject:CMLabel(text, NO, YES)];
        [rows addObject:control];
    } else {
        [rows addObject:CMRow(CMLabel(text, NO, NO), control)];
    }

    id note = item[@"note"];
    if ([note isKindOfClass:[NSString class]] && [(NSString *)note length] > 0) {
        [rows addObject:CMLabel((NSString *)note, NO, YES)];
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
     *   実機で最初に踏んだのがまさにこれ。
     */
    for (NSView *row in rows) {
        [row.widthAnchor constraintEqualToAnchor:group.widthAnchor].active = YES;
    }
    return group;
}

static void CMApplySchema(NSDictionary *root)
{
    if (gStack == nil) return;

    /* ★ 記録中に組み直すとモニタが宙に浮く。先に止める */
    CMStopRecording();

    id strings = root[@"strings"];
    gStrings = [strings isKindOfClass:[NSDictionary class]] ? (NSDictionary *)strings : nil;

    for (NSView *view in [gStack.views copy]) [gStack removeView:view];

    id items = root[@"items"];
    if ([items isKindOfClass:[NSArray class]]) {
        for (id raw in (NSArray *)items) {
            if (![raw isKindOfClass:[NSDictionary class]]) continue;
            NSView *view = CMBuildItem((NSDictionary *)raw);
            if (view == nil) continue;
            [gStack addView:view inGravity:NSStackViewGravityTop];
            /* ★ 外側でも同じ（→ CMBuildItem の ★★）。edgeInsets のぶんを引く */
            [view.widthAnchor
                constraintEqualToAnchor:gStack.widthAnchor
                               constant:-(gStack.edgeInsets.left + gStack.edgeInsets.right)].active = YES;
        }
    }

    id title = root[@"title"];
    if (gPanel != nil && [title isKindOfClass:[NSString class]]) gPanel.title = (NSString *)title;
}

static NSDictionary *CMParseSchema(const char *schemaJson)
{
    if (schemaJson == NULL) return nil;
    NSData *data = [@(schemaJson) dataUsingEncoding:NSUTF8StringEncoding];
    if (data == nil) return nil;

    NSError *error = nil;
    id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:&error];
    if (![root isKindOfClass:[NSDictionary class]]) {
        CMEmitLog([[NSString stringWithFormat:@"設定の JSON を読めませんでした: %@",
                    error.localizedDescription] UTF8String]);
        return nil;
    }
    return (NSDictionary *)root;
}

static void CMEnsurePanel(void)
{
    if (gPanel != nil) return;
    if (gTarget == nil) gTarget = [[CMSettingsTarget alloc] init];

    NSRect frame = NSMakeRect(0, 0, 520, 640);
    /*
     * ★ nonactivatingPanel を入れないこと（→ ファイル冒頭）。
     * ★ utilityWindow にしておくと、他のアプリを前に出したときに一緒に引っ込む
     *   —— 常駐マスコットの設定としてはそちらが素直。
     */
    gPanel = [[NSPanel alloc] initWithContentRect:frame
                                        styleMask:(NSWindowStyleMaskTitled |
                                                   NSWindowStyleMaskClosable |
                                                   NSWindowStyleMaskResizable |
                                                   NSWindowStyleMaskUtilityWindow)
                                          backing:NSBackingStoreBuffered
                                            defer:YES];
    gPanel.releasedWhenClosed = NO;   /* ★ 閉じても捨てない。次に開くときに作り直さない */
    gPanel.hidesOnDeactivate = NO;
    gPanel.delegate = gTarget;

    /*
     * ★★ setFrameAutosaveName: だけでは復元されない。 保存はされるが、読み戻すのは
     *   setFrameUsingName:。付け忘れると、毎回 initWithContentRect の矩形
     *   （= AppKit は bottom-up なので**画面の左下**）に出る。
     *   「ユーザーが動かしたのに次に開くと左下へ戻る」という形で出る。
     */
    gPanel.frameAutosaveName = @"ChatterMascotSettingsPanel";
    if (![gPanel setFrameUsingName:gPanel.frameAutosaveName]) [gPanel center];

    gStack = [[NSStackView alloc] init];
    gStack.orientation = NSUserInterfaceLayoutOrientationVertical;
    gStack.alignment = NSLayoutAttributeLeading;
    gStack.spacing = 10;
    gStack.edgeInsets = NSEdgeInsetsMake(16, 16, 16, 16);
    gStack.translatesAutoresizingMaskIntoConstraints = NO;

    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:frame];
    scroll.hasVerticalScroller = YES;
    scroll.autohidesScrollers = YES;
    scroll.drawsBackground = NO;
    scroll.translatesAutoresizingMaskIntoConstraints = NO;
    scroll.documentView = gStack;

    NSView *content = gPanel.contentView;
    [content addSubview:scroll];
    [NSLayoutConstraint activateConstraints:@[
        [scroll.leadingAnchor constraintEqualToAnchor:content.leadingAnchor],
        [scroll.trailingAnchor constraintEqualToAnchor:content.trailingAnchor],
        [scroll.topAnchor constraintEqualToAnchor:content.topAnchor],
        [scroll.bottomAnchor constraintEqualToAnchor:content.bottomAnchor],
        /* ★ 横幅をクリップ領域に合わせること。合わせないと折り返しが効かず横スクロールになる */
        [gStack.widthAnchor constraintEqualToAnchor:scroll.contentView.widthAnchor],
        [gStack.topAnchor constraintEqualToAnchor:scroll.contentView.topAnchor],
        [gStack.leadingAnchor constraintEqualToAnchor:scroll.contentView.leadingAnchor],
    ]];
}

/* ── C# から呼ぶ口 ─────────────────────────────────────────── */

bool CM_SettingsPanelShow(const char* schemaJson)
{
    /* ★ 非メインからは成功を騙らない（→ CMNative.h の契約） */
    if (!CMIsMainThread()) {
        CMEmitLog("CM_SettingsPanelShow をメインスレッド以外から呼びました");
        return false;
    }

    NSDictionary *root = CMParseSchema(schemaJson);
    if (root == nil) return false;

    CMRunOnMain(^{
        CMEnsurePanel();
        CMApplySchema(root);
        if (gPanel.frameAutosaveName.length == 0 || NSIsEmptyRect(gPanel.frame)) [gPanel center];

        /*
         * ★★ activateIgnoringOtherApps を忘れないこと。 LSUIElement のアプリは
         *   既定でアクティブになれないので、これが無いと
         *   「スライダーは動くがショートカットの記録が始まらない」という形で出る
         *   （キー入力はキーウィンドウにしか届かない）。
         */
        [NSApp activateIgnoringOtherApps:YES];
        [gPanel makeKeyAndOrderFront:nil];

        /*
         * ★ 出せたかどうかを1行残すこと。 「開かない」は
         *   （a）Show が失敗した（b）画面外に出た（c）他のウィンドウの背後に居る
         *   のどれかで、手当てが全部違う。NSStatusItem で同じところを踏んでいる
         *   （→ docs/mascot.md）。
         * ★ frame はここで測ってよい。 makeKeyAndOrderFront: の後なので確定している
         *   （レイアウトが次の run loop に回る NSStatusItem とは事情が違う）。
         */
        CMEmitLog([[NSString stringWithFormat:
            @"設定パネル: frame=%.0f,%.0f %.0fx%.0f visible=%d key=%d screen=%@",
            gPanel.frame.origin.x, gPanel.frame.origin.y,
            gPanel.frame.size.width, gPanel.frame.size.height,
            (int)gPanel.isVisible, (int)gPanel.isKeyWindow,
            gPanel.screen == nil ? @"なし" : NSStringFromRect(gPanel.screen.frame)] UTF8String]);
    });
    return true;
}

bool CM_SettingsPanelUpdate(const char* schemaJson)
{
    if (!CMIsMainThread()) {
        CMEmitLog("CM_SettingsPanelUpdate をメインスレッド以外から呼びました");
        return false;
    }
    if (gPanel == nil || !gPanel.isVisible) return false;

    NSDictionary *root = CMParseSchema(schemaJson);
    if (root == nil) return false;

    CMRunOnMain(^{ CMApplySchema(root); });
    return true;
}

void CM_SettingsPanelHide(void)
{
    CMRunOnMain(^{
        CMStopRecording();
        if (gPanel != nil) [gPanel orderOut:nil];
    });
}

bool CM_SettingsPanelIsVisible(void)
{
    if (!CMIsMainThread()) return false;
    return gPanel != nil && gPanel.isVisible;
}
