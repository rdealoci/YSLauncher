#include <spawn.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <mach-o/dyld.h>
#include <limits.h>
#include <sys/wait.h>

extern int responsibility_spawnattrs_setdisclaim(posix_spawnattr_t *attrs, int disclaim);
extern char **environ;

int main(int argc, char **argv, char **envp) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <executable> [args...]\n", argv[0]);
        return 1;
    }

    char exec_path[PATH_MAX] = {0};
    uint32_t size = sizeof(exec_path);
    if (_NSGetExecutablePath(exec_path, &size) == 0) {
        char *last_slash = strrchr(exec_path, '/');
        if (last_slash) {
            *(last_slash + 1) = '\0';
            char dylib_path[PATH_MAX];
            snprintf(dylib_path, sizeof(dylib_path), "%slibnomicrophone.dylib", exec_path);
            if (access(dylib_path, R_OK) == 0) {
                char *existing = getenv("DYLD_INSERT_LIBRARIES");
                if (existing && strlen(existing) > 0) {
                    if (strstr(existing, "libnomicrophone.dylib") == NULL) {
                        char combined[PATH_MAX * 2];
                        snprintf(combined, sizeof(combined), "%s:%s", dylib_path, existing);
                        setenv("DYLD_INSERT_LIBRARIES", combined, 1);
                    }
                } else {
                    setenv("DYLD_INSERT_LIBRARIES", dylib_path, 1);
                }
            }
        }
    }

    posix_spawnattr_t attr;
    posix_spawnattr_init(&attr);
    responsibility_spawnattrs_setdisclaim(&attr, 1);

    pid_t pid;
    int ret = posix_spawnp(&pid, argv[1], NULL, &attr, &argv[1], environ);
    posix_spawnattr_destroy(&attr);

    if (ret != 0) {
        perror("posix_spawnp");
        return ret;
    }

    int status = 0;
    if (waitpid(pid, &status, 0) < 0) {
        perror("waitpid");
        return 1;
    }
    if (WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }
    if (WIFSIGNALED(status)) {
        return 128 + WTERMSIG(status);
    }
    return 1;
}
