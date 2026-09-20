/*
 * ライフサイクルと NSApplicationActivationPolicy。
 */
#import <Cocoa/Cocoa.h>
#import "CMNative.h"

/*
 * ★ バンドルを差し替えたのに古いものが読まれている、を見分けるためだけに存在する。
 *   .bundle は git に入れない（レビューできず、ソースとの一致を CI で検証できない）ので、
 *   「ビルドし忘れた古いバンドル」は普通に起こる。
 */
static const char* kCMVersion = "1";

static bool gInitialized = false;

bool CM_Initialize(void)
{
    /* ★ 冪等。ドメインリロードで2回呼ばれても壊れないこと */
    if (gInitialized) return true;
    gInitialized = true;
    return true;
}

void CM_Shutdown(void)
{
    if (!gInitialized) return;
    CM_StatusItemHide();
    gInitialized = false;
}

const char* CM_Version(void)
{
    /* ★ strdup しない。 呼び出し側（C#）に解放させないための静的領域 */
    return kCMVersion;
}

void CM_SetActivationPolicy(int policy)
{
    CMRunOnMain(^{
        NSApplicationActivationPolicy value = (policy == 1)
            ? NSApplicationActivationPolicyAccessory
            : NSApplicationActivationPolicyRegular;
        [NSApp setActivationPolicy:value];
    });
}

/* → CMNative.h */
void CM_Beep(void)
{
    CMRunOnMain(^{
        NSBeep();
    });
}
