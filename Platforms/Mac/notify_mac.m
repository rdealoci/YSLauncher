#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"

@interface ToastDelegate : NSObject <NSUserNotificationCenterDelegate>
@property (nonatomic, copy) NSString *postUrl;
@property (nonatomic, copy) NSString *exePath;
@end

@implementation ToastDelegate

- (BOOL)userNotificationCenter:(NSUserNotificationCenter *)center
     shouldPresentNotification:(NSUserNotification *)notification {
    return YES;
}

- (void)userNotificationCenter:(NSUserNotificationCenter *)center
       didActivateNotification:(NSUserNotification *)notification {
    if (notification.activationType == NSUserNotificationActivationTypeActionButtonClicked) {
        if (self.postUrl.length > 0) {
            NSURL *url = [NSURL URLWithString:self.postUrl];
            if (url) {
                [[NSWorkspace sharedWorkspace] openURL:url];
            }
        }
    } else if (notification.activationType == NSUserNotificationActivationTypeContentsClicked) {
        NSString *appPath = nil;
        NSRange appRange = [self.exePath rangeOfString:@".app" options:NSCaseInsensitiveSearch];
        if (appRange.location != NSNotFound) {
            appPath = [self.exePath substringToIndex:appRange.location + 4];
        }
        if (appPath && [[NSFileManager defaultManager] fileExistsAtPath:appPath]) {
            [[NSWorkspace sharedWorkspace] openURL:[NSURL fileURLWithPath:appPath]];
        } else if ([[NSFileManager defaultManager] fileExistsAtPath:@"/Applications/YSLauncher.app"]) {
            [[NSWorkspace sharedWorkspace] openURL:[NSURL fileURLWithPath:@"/Applications/YSLauncher.app"]];
        } else {
            [[NSWorkspace sharedWorkspace] openURL:[NSURL fileURLWithPath:self.exePath]];
        }
    }
    exit(0);
}

@end

int main(int argc, const char * argv[]) {
    @autoreleasepool {
        if (argc < 6) {
            fprintf(stderr, "Usage: %s <title> <body> <url> <exe> <btn_post> [btn_launch] [subtitle] [image_path]\n", argv[0]);
            return 1;
        }

        NSString *title = [NSString stringWithUTF8String:argv[1]];
        NSString *body = [NSString stringWithUTF8String:argv[2]];
        NSString *postUrl = [NSString stringWithUTF8String:argv[3]];
        NSString *exePath = [NSString stringWithUTF8String:argv[4]];
        NSString *btnPost = [NSString stringWithUTF8String:argv[5]];
        NSString *btnLaunch = argc > 6 ? [NSString stringWithUTF8String:argv[6]] : @"Abrir launcher";
        NSString *subtitle = (argc > 7 && strlen(argv[7]) > 0) ? [NSString stringWithUTF8String:argv[7]] : nil;

        NSUserNotification *notification = [[NSUserNotification alloc] init];
        notification.title = title;
        if (subtitle && subtitle.length > 0) {
            notification.subtitle = subtitle;
        }
        notification.informativeText = body;
        notification.soundName = NSUserNotificationDefaultSoundName;
        notification.hasActionButton = YES;
        notification.actionButtonTitle = btnPost;
        notification.otherButtonTitle = btnLaunch;

        NSUserNotificationCenter *center = [NSUserNotificationCenter defaultUserNotificationCenter];
        ToastDelegate *delegate = [[ToastDelegate alloc] init];
        delegate.postUrl = postUrl;
        delegate.exePath = exePath;
        center.delegate = delegate;

        [center deliverNotification:notification];

        // Keep process running so delegate can handle user clicks
        [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:600]];
    }
    return 0;
}

#pragma clang diagnostic pop
