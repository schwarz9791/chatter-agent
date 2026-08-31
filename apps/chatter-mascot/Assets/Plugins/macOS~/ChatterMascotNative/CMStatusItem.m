/*
 * メニューバー（ステータスバー）の常駐アイコンとメニュー。
 *
 * ★ このファイルにメニューのキーもラベルも1つも書かないこと。 JSON を読んで組み、
 *   押されたら representedObject に載せた key を返すだけ。項目の追加・並び替え・
 *   ラベルの変更が C# だけの変更で済むことが、この作りを選んだ理由そのもの
 *   （#76 の設定パネルが同じ形で乗る）。
 */
#import <Cocoa/Cocoa.h>
#import "CMNative.h"

@interface CMStatusItemTarget : NSObject
- (void)onMenuItem:(id)sender;
@end

@implementation CMStatusItemTarget
- (void)onMenuItem:(id)sender
{
    NSMenuItem *item = (NSMenuItem *)sender;
    id key = item.representedObject;
    if (![key isKindOfClass:[NSString class]]) return;
    /*
     * ★ ここは Unity のプレイヤーループの外（メニュー追跡中のネストした run loop）で
     *   走りうる。C# 側は積むだけにしてあること。
     */
    CMEmitEvent("menu", [(NSString *)key UTF8String]);
}
@end

static NSStatusItem *gStatusItem = nil;
static CMStatusItemTarget *gTarget = nil;

static NSDictionary *CMParseMenuJson(const char *menuJson)
{
    if (menuJson == NULL) return nil;
    NSData *data = [@(menuJson) dataUsingEncoding:NSUTF8StringEncoding];
    if (data == nil) return nil;

    NSError *error = nil;
    id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:&error];
    if (![root isKindOfClass:[NSDictionary class]]) {
        CMEmitLog([[NSString stringWithFormat:@"メニューの JSON を読めませんでした: %@",
                    error.localizedDescription] UTF8String]);
        return nil;
    }
    return (NSDictionary *)root;
}

/*
 * ★ @1x と @2x を1つの NSImage に入れること。 片方だけだと Retina でぼやけるか、
 *   非 Retina で 2 倍の大きさに描かれる。
 * ★ すべての rep の size をポイントに揃えること。 NSBitmapImageRep の size は既定で
 *   画素数（@2x なら 32pt）になっており、揃えないと NSImage が解像度で選べない。
 */
static NSImage *CMLoadTemplateImage(NSDictionary *icon)
{
    if (![icon isKindOfClass:[NSDictionary class]]) return nil;

    NSMutableArray<NSImageRep *> *reps = [NSMutableArray array];
    for (NSString *scale in @[@"1x", @"2x"]) {
        id path = icon[scale];
        if (![path isKindOfClass:[NSString class]] || [(NSString *)path length] == 0) continue;

        NSData *data = [NSData dataWithContentsOfFile:(NSString *)path];
        if (data == nil) {
            CMEmitLog([[NSString stringWithFormat:@"アイコンを読めませんでした: %@", path] UTF8String]);
            continue;
        }
        NSImageRep *rep = [NSBitmapImageRep imageRepWithData:data];
        if (rep != nil) [reps addObject:rep];
    }
    if (reps.count == 0) return nil;

    NSInteger widthPoints = NSIntegerMax;
    NSInteger heightPoints = NSIntegerMax;
    for (NSImageRep *rep in reps) {
        if (rep.pixelsWide < widthPoints) widthPoints = rep.pixelsWide;
        if (rep.pixelsHigh < heightPoints) heightPoints = rep.pixelsHigh;
    }

    NSSize size = NSMakeSize((CGFloat)widthPoints, (CGFloat)heightPoints);
    NSImage *image = [[NSImage alloc] initWithSize:size];
    for (NSImageRep *rep in reps) {
        [rep setSize:size];
        [image addRepresentation:rep];
    }

    /* ★ テンプレートにするとダーク/ライトとメニューバーの選択状態に自動で追従する */
    [image setTemplate:YES];
    return image;
}

static void CMApplyMenu(NSDictionary *root)
{
    if (gStatusItem == nil) return;

    NSStatusBarButton *button = gStatusItem.button;
    if (button != nil) {
        id tooltip = root[@"tooltip"];
        if ([tooltip isKindOfClass:[NSString class]]) button.toolTip = (NSString *)tooltip;

        NSImage *image = CMLoadTemplateImage(root[@"icon"]);
        if (image != nil) button.image = image;

        /*
         * ★ ミュート中はアイコンを薄くする。 別画像を持たずに状態を目に見せるため。
         *   ミュートを永続化する以上、状態が分かることが事故防止の本体になる。
         */
        button.appearsDisabled = [root[@"dimmed"] boolValue];
    }

    NSMenu *menu = [[NSMenu alloc] init];
    /* ★ NO にすること。 YES のままだと AppKit が勝手に判定して項目が灰色になる */
    menu.autoenablesItems = NO;

    id items = root[@"items"];
    if ([items isKindOfClass:[NSArray class]]) {
        for (id raw in (NSArray *)items) {
            if (![raw isKindOfClass:[NSDictionary class]]) continue;
            NSDictionary *item = (NSDictionary *)raw;

            if ([item[@"separator"] boolValue]) {
                [menu addItem:[NSMenuItem separatorItem]];
                continue;
            }

            id label = item[@"label"];
            id key = item[@"key"];
            if (![label isKindOfClass:[NSString class]]) continue;
            if (![key isKindOfClass:[NSString class]]) continue;

            NSMenuItem *entry = [[NSMenuItem alloc] initWithTitle:(NSString *)label
                                                           action:@selector(onMenuItem:)
                                                    keyEquivalent:@""];
            entry.target = gTarget;
            entry.representedObject = key;
            entry.state = [item[@"checked"] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;

            id enabled = item[@"enabled"];
            entry.enabled = (enabled == nil) ? YES : [enabled boolValue];

            [menu addItem:entry];
        }
    }

    gStatusItem.menu = menu;
}

bool CM_StatusItemShow(const char* menuJson)
{
    NSDictionary *root = CMParseMenuJson(menuJson);
    if (root == nil) return false;

    __block bool ok = true;
    CMRunOnMain(^{
        if (gTarget == nil) gTarget = [[CMStatusItemTarget alloc] init];
        if (gStatusItem == nil) {
            gStatusItem = [[NSStatusBar systemStatusBar]
                statusItemWithLength:NSSquareStatusItemLength];
            if (gStatusItem == nil) {
                CMEmitLog("ステータスバーに場所を取れませんでした");
                ok = false;
                return;
            }
            /*
             * ★ behavior を触らないこと。 NSStatusItemBehaviorTerminationOnRemoval を
             *   立てると、ユーザーが ⌘ドラッグでアイコンを外しただけでアプリが終了する。
             *   Dock に居ない常駐アプリではそれが「黙って消えた」にしか見えない。
             */
            /*
             * ユーザーが ⌘ドラッグで並べ替えた位置を覚えさせる。
             *
             * ★ メニューバー管理ツール（Thaw / Bartender / Ice など）が隠すのは
             *   これでは防げない。あの種のツールはアイコンを画面外の負の座標へ
             *   移動させるので、frame.x が負なら「隠されている」と読む
             *   （→ docs/mascot.md）。押し戻す API は無い。
             */
            gStatusItem.autosaveName = @"ChatterMascotStatusItem";
            gStatusItem.visible = YES;
        }
        CMApplyMenu(root);

        // ★ 出せたかどうかを必ず1行残すこと。 「メニューバーに出ない」は
        //   ここが失敗したのか、アイコンが読めなかったのか、OS が場所を空けなかったのかで
        //   手当てが違う。★ frame をここで測っても意味が無い（レイアウトは次の run loop で
        //   走るので、この時点では必ず高さ 0 が返る）。位置が要るときは
        //   CMEmitLog を dispatch_after で足して測ること
        CMEmitLog([[NSString stringWithFormat:
            @"ステータスバー: item=%@ button=%@ image=%@ visible=%d",
            gStatusItem == nil ? @"なし" : @"あり",
            gStatusItem.button == nil ? @"なし" : @"あり",
            gStatusItem.button.image == nil ? @"なし" : @"あり",
            (int)gStatusItem.visible] UTF8String]);

    });
    return ok;
}

bool CM_StatusItemUpdate(const char* menuJson)
{
    NSDictionary *root = CMParseMenuJson(menuJson);
    if (root == nil) return false;
    if (gStatusItem == nil) return false;

    CMRunOnMain(^{ CMApplyMenu(root); });
    return true;
}

void CM_StatusItemHide(void)
{
    CMRunOnMain(^{
        if (gStatusItem == nil) return;
        [[NSStatusBar systemStatusBar] removeStatusItem:gStatusItem];
        gStatusItem = nil;
    });
}
