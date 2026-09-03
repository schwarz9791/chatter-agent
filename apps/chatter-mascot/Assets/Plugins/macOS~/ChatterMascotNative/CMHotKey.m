/*
 * グローバルショートカット。
 *
 * ★ Carbon の RegisterEventHotKey を使うこと。 NSEvent の
 *   addGlobalMonitorForEvents と違い、アクセシビリティ権限のダイアログが出ない。
 *   常駐マスコットのために「入力の監視」を許可させるのは要求として重すぎる。
 *
 * ★ Carbon の仮想キーコードは物理キーの位置。 レイアウトに依らない。
 *   kVK_ANSI_M は JIS 配列でも同じ物理キーに M が刻印されているので実用上は一致するが、
 *   Dvorak などでは刻印と合わない。C# 側（HotKeySpec）の doc に書いてある。
 */
#import <Cocoa/Cocoa.h>
#import <Carbon/Carbon.h>
#import "CMNative.h"

static const OSType kCMHotKeySignature = 'CHMS';

@interface CMHotKeyEntry : NSObject
@property (nonatomic, assign) EventHotKeyRef ref;   /* 中断中は NULL */
@property (nonatomic, assign) UInt32 keyCode;
@property (nonatomic, assign) UInt32 modifiers;
@end

@implementation CMHotKeyEntry
@end

/* id（C# が割り当てた整数）→ EventHotKeyRef */
static NSMutableDictionary<NSNumber *, CMHotKeyEntry *> *gHotKeys = nil;
static EventHandlerRef gHandler = NULL;

/* ショートカットの記録中か（→ CM_HotKeySuspend） */
static bool gSuspended = false;

static OSStatus CMHotKeyHandler(EventHandlerCallRef next, EventRef event, void *userData)
{
    (void)next;
    (void)userData;

    EventHotKeyID hotKeyId;
    OSStatus status = GetEventParameter(
        event, kEventParamDirectObject, typeEventHotKeyID,
        NULL, sizeof(hotKeyId), NULL, &hotKeyId);
    if (status != noErr) return status;
    if (hotKeyId.signature != kCMHotKeySignature) return eventNotHandledErr;

    /*
     * ★ ここから Unity API を呼ばないこと（C# 側の話）。 このハンドラは
     *   アプリのイベントターゲットから呼ばれ、Unity のプレイヤーループの外で走りうる。
     */
    CMEmitHotKey((int)hotKeyId.id);
    return noErr;
}

static bool CMEnsureHandler(void)
{
    if (gHandler != NULL) return true;

    EventTypeSpec spec;
    spec.eventClass = kEventClassKeyboard;
    spec.eventKind = kEventHotKeyPressed;

    OSStatus status = InstallApplicationEventHandler(
        &CMHotKeyHandler, 1, &spec, NULL, &gHandler);
    if (status != noErr) {
        CMEmitLog([[NSString stringWithFormat:
            @"ホットキーのハンドラを入れられませんでした: %d", (int)status] UTF8String]);
        gHandler = NULL;
        return false;
    }
    return true;
}

int CM_HotKeyRegister(int id, unsigned int keyCode, unsigned int modifiers)
{
    /*
     * ★ 修飾キー無しを受け付けないこと。 単独のキーを登録すると、そのキーが
     *   どのアプリでも入力できなくなる。C# 側（HotKeySpec）でも弾いているが、
     *   ABI を直接叩かれても壊れないよう二重にしておく。
     */
    if (modifiers == 0) return paramErr;

    /*
     * ★ 非メインからは成功を騙らない（→ CMNative.h の契約）。
     *   ここで noErr を返すと、C# 側が registered = true にして
     *   「ショートカット: ⌃⌥M」とログまで出すのに、実際には何も登録されていない。
     */
    if (!CMIsMainThread()) {
        CMEmitLog("CM_HotKeyRegister をメインスレッド以外から呼びました");
        return eventInternalErr;
    }

    __block int result = noErr;
    CMRunOnMain(^{
        if (!CMEnsureHandler()) {
            result = eventInternalErr;
            return;
        }
        if (gHotKeys == nil) gHotKeys = [NSMutableDictionary dictionary];

        /* 同じ id の登録が残っていれば先に外す（冪等にするため） */
        CM_HotKeyUnregister(id);

        EventHotKeyID hotKeyId;
        hotKeyId.signature = kCMHotKeySignature;
        hotKeyId.id = (UInt32)id;

        CMHotKeyEntry *entry = [[CMHotKeyEntry alloc] init];
        entry.keyCode = (UInt32)keyCode;
        entry.modifiers = (UInt32)modifiers;
        entry.ref = NULL;

        /*
         * ★★ 中断中は覚えるだけにすること。 ここで実際に登録すると、
         *   記録中に settings.json が外から書き換わった（PumpSettings → RegisterHotKeys）
         *   だけで **記録が黙って壊れる**（Carbon がキーを先に取るのでモニタに届かない）。
         *   登録は CM_HotKeyResume がまとめて行う。
         */
        if (gSuspended) {
            gHotKeys[@(id)] = entry;
            return;
        }

        EventHotKeyRef ref = NULL;
        OSStatus status = RegisterEventHotKey(
            (UInt32)keyCode, (UInt32)modifiers, hotKeyId,
            GetApplicationEventTarget(), 0, &ref);
        if (status != noErr || ref == NULL) {
            /* -9878 = eventHotKeyExistsErr。他のアプリが同じ組み合わせを取っている */
            result = (status != noErr) ? (int)status : (int)eventInternalErr;
            return;
        }

        entry.ref = ref;
        gHotKeys[@(id)] = entry;
    });
    return result;
}

void CM_HotKeyUnregister(int id)
{
    CMRunOnMain(^{
        if (gHotKeys == nil) return;
        CMHotKeyEntry *entry = gHotKeys[@(id)];
        if (entry == nil) return;

        /* ★ 中断中は ref が無い（覚えているだけ）ので、外すものが無い */
        if (entry.ref != NULL) UnregisterEventHotKey(entry.ref);
        [gHotKeys removeObjectForKey:@(id)];
    });
}

/*
 * ショートカットの記録中だけ、グローバルショートカットを外す（#76）。
 *
 * ★★ ハンドラ側で握り潰すのでは駄目。 登録が残っている限り Carbon が**先に**キーを取り、
 *   アプリの通常のイベント経路に流れないので、**記録のローカルモニタに1つも届かない**。
 *   症状は「記録を始めて既存のショートカット（⌃⌥H）を押すと、記録は終わらずに
 *   キャラクターが隠れる」——押した本人からは「記録が効かない」にしか見えない。
 *   実際に UnregisterEventHotKey すること。
 *
 * ★ 組み合わせは覚えておいて CM_HotKeyResume で登録し直す。C# に登録し直させると、
 *   「いま記録中か」をあちらが知る必要が出てくる（記録の開始はネイティブ内で完結している）。
 *
 * ★ 冪等。CMStartRecording は CMStopRecording を通ってから始まるので、
 *   中断→中断 / 再開→再開 が普通に起きる。
 */
void CM_HotKeySuspend(void)
{
    CMRunOnMain(^{
        if (gSuspended) return;
        gSuspended = true;

        for (NSNumber *id in gHotKeys) {
            CMHotKeyEntry *entry = gHotKeys[id];
            if (entry.ref == NULL) continue;
            UnregisterEventHotKey(entry.ref);
            entry.ref = NULL;
        }
    });
}

void CM_HotKeyResume(void)
{
    CMRunOnMain(^{
        if (!gSuspended) return;
        gSuspended = false;

        for (NSNumber *id in gHotKeys) {
            CMHotKeyEntry *entry = gHotKeys[id];
            if (entry.ref != NULL) continue;
            if (!CMEnsureHandler()) return;

            EventHotKeyID hotKeyId;
            hotKeyId.signature = kCMHotKeySignature;
            hotKeyId.id = (UInt32)id.intValue;

            EventHotKeyRef ref = NULL;
            OSStatus status = RegisterEventHotKey(
                entry.keyCode, entry.modifiers, hotKeyId,
                GetApplicationEventTarget(), 0, &ref);
            if (status != noErr || ref == NULL) {
                /* ★ ここで消さないこと。次の再開でもう一度試せる */
                CMEmitLog([[NSString stringWithFormat:
                    @"ショートカットを戻せませんでした (id=%d status=%d)",
                    id.intValue, (int)status] UTF8String]);
                continue;
            }
            entry.ref = ref;
        }
    });
}
