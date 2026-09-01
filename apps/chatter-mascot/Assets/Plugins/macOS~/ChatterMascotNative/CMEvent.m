/*
 * C# へ返す唯一の口。
 *
 * ★ ここが「1本」であることに意味がある（→ CMNative.h）。menu / hotkey が増えても
 *   C# 側で GC から守るデリゲートは1つのままになる。
 */
#import <Foundation/Foundation.h>
#import "CMNative.h"

static CM_EventCallback gCallback = NULL;

void CM_SetEventCallback(CM_EventCallback cb)
{
    gCallback = cb;
}

bool CMIsMainThread(void)
{
    return [NSThread isMainThread];
}

void CMRunOnMain(void (^block)(void))
{
    if (block == nil) return;
    if ([NSThread isMainThread]) {
        block();
        return;
    }
    /*
     * ★ dispatch_sync にしないこと。 Unity のメインスレッドから呼ばれる前提の API なので
     *   通常ここには来ないが、来たときに相手が main を待っていると即デッドロックになる。
     */
    dispatch_async(dispatch_get_main_queue(), block);
}

/*
 * ★ JSON を手で組み立てないこと。 label に入りうる文字（" や \）のエスケープを
 *   自前で持つ理由が無い。ここは key しか載らないが、書き方を1つに保つ。
 */
static void CMEmit(NSDictionary *payload)
{
    CM_EventCallback cb = gCallback;
    /* ★ 取り外し済みなら黙って捨てる。終了処理の途中で menu action が届くのは正常 */
    if (cb == NULL) return;

    @autoreleasepool {
        NSError *error = nil;
        NSData *data = [NSJSONSerialization dataWithJSONObject:payload options:0 error:&error];
        if (data == nil) {
            /* ★ ここで CMEmitLog を呼ばないこと（同じ経路なので無限に回る） */
            return;
        }
        NSString *json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
        if (json == nil) return;

        /*
         * ★ UTF8String の寿命は autorelease pool の中だけ。C# 側のマーシャラは
         *   呼び出しの間にコピーするので、この寿命で足りる。
         */
        cb([json UTF8String]);
    }
}

void CMEmitEvent(const char* type, const char* key)
{
    if (type == NULL) return;
    NSMutableDictionary *payload = [NSMutableDictionary dictionary];
    payload[@"type"] = @(type);
    if (key != NULL) payload[@"key"] = @(key);
    CMEmit(payload);
}

void CMEmitHotKey(int hotKeyId)
{
    CMEmit(@{ @"type": @"hotkey", @"id": @(hotKeyId) });
}

void CMEmitLog(const char* message)
{
    if (message == NULL) return;
    CMEmit(@{ @"type": @"log", @"message": @(message) });
}
