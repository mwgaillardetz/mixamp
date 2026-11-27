#include <dlfcn.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern int _NSGetExecutablePath(char *, uint32_t *);
typedef struct { size_t size; const char *host_path; const char *dotnet_root; } host_parameters;
typedef int32_t (*init_fn)(int, const char **, const host_parameters *, void **);
typedef int32_t (*run_fn)(void *);
typedef int32_t (*close_fn)(void *);

int main(int argc, char **argv) {
    char input[PATH_MAX], host[PATH_MAX], root[PATH_MAX], lib[PATH_MAX], assembly[PATH_MAX];
    uint32_t size = sizeof(input);
    if (_NSGetExecutablePath(input,&size) || !realpath(input,host)) return 1;
    char *name = strrchr(input,'/');
    if (!name) return 1;
    *name=0;
    if (snprintf(lib,sizeof(lib),"%s/../Resources/runtime",input) >= sizeof(lib) || !realpath(lib,root)) return 1;
    if (snprintf(lib,sizeof(lib),"%s/libhostfxr.dylib",root) >= sizeof(lib) ||
        snprintf(assembly,sizeof(assembly),"%s/Mixamp.dll",root) >= sizeof(assembly)) return 1;
    void *library=dlopen(lib,RTLD_NOW|RTLD_LOCAL);
    if (!library) { fprintf(stderr,"%s\n",dlerror()); return 1; }
    init_fn initialize=(init_fn)dlsym(library,"hostfxr_initialize_for_dotnet_command_line");
    run_fn run=(run_fn)dlsym(library,"hostfxr_run_app");
    close_fn close_host=(close_fn)dlsym(library,"hostfxr_close");
    if (!initialize || !run || !close_host) return 1;
    host_parameters parameters={sizeof(parameters),host,root};
    argv[0]=assembly;
    void *context=NULL;
    int result=initialize(argc,(const char **)argv,&parameters,&context);
    if (result < 0 || !context) return 1;
    result=run(context);
    close_host(context);
    return result;
}
