#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <userenv.h>
#include <shlobj.h>
#include <sddl.h>
typedef HRESULT (WINAPI *CREATE_ENV)(const BYTE*,DWORD,DWORD,HANDLE*);
typedef void (WINAPI *CLOSE_ENV)(HANDLE);
int wmain(int argc, wchar_t **argv) {
    FILE *f; long length; BYTE *bytes; HRESULT hr; HANDLE env=NULL, job=NULL, out=NULL, input=NULL;
    HMODULE module; CREATE_ENV create; CLOSE_ENV close_env;
    STARTUPINFOEXW startup={0}; PROCESS_INFORMATION process={0}; SIZE_T size=0;
    SECURITY_ATTRIBUTES security={sizeof(security),NULL,TRUE}; HANDLE handles[2];
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits={0}; DWORD exit_code=0;
    JOBOBJECT_CPU_RATE_CONTROL_INFORMATION cpu_limits={0};
    wchar_t command[8192]; int result=1;
    LPWCH environment=NULL;
    if(argc!=7 && argc!=8 && argc!=9) return 64;
    f=_wfopen(argv[1],L"rb"); if(!f) return 65;
    fseek(f,0,SEEK_END); length=ftell(f); rewind(f);
    if(length<=0 || length>1048576) { fclose(f); return 66; }
    bytes=(BYTE*)malloc((size_t)length); if(!bytes) { fclose(f); return 67; }
    if(fread(bytes,1,(size_t)length,f)!=(size_t)length) { fclose(f); free(bytes); return 68; }
    fclose(f);
    module=LoadLibraryExW(L"processmodel.dll",NULL,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!module) { free(bytes); return 69; }
    create=(CREATE_ENV)(void*)GetProcAddress(module,"CreateProcessSecurityEnvironment");
    close_env=(CLOSE_ENV)(void*)GetProcAddress(module,"CloseProcessSecurityEnvironment");
    if(!create || !close_env) { free(bytes); FreeLibrary(module); return 70; }
    hr=create(bytes,(DWORD)length,0,&env); free(bytes);
    printf("CreateProcessSecurityEnvironment HRESULT=0x%08lX\n",(unsigned long)hr);
    if(FAILED(hr)) { FreeLibrary(module); return 71; }
    InitializeProcThreadAttributeList(NULL,2,0,&size);
    startup.lpAttributeList=(LPPROC_THREAD_ATTRIBUTE_LIST)malloc(size);
    if(!startup.lpAttributeList || !InitializeProcThreadAttributeList(startup.lpAttributeList,2,0,&size)) goto done;
    if(!UpdateProcThreadAttribute(startup.lpAttributeList,0,0x20023,&env,sizeof(env),NULL,NULL)) goto done;
    out=CreateFileW(argv[6],GENERIC_WRITE,FILE_SHARE_READ,&security,CREATE_ALWAYS,0,NULL);
    input=CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,0,NULL);
    if(out==INVALID_HANDLE_VALUE || input==INVALID_HANDLE_VALUE) goto done;
    handles[0]=out; handles[1]=input;
    if(!UpdateProcThreadAttribute(startup.lpAttributeList,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,handles,sizeof(handles),NULL,NULL)) goto done;
    startup.StartupInfo.cb=sizeof(startup); startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput=out; startup.StartupInfo.hStdError=out; startup.StartupInfo.hStdInput=input;
    job=CreateJobObjectW(NULL,NULL); if(!job) goto done;
    limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
        JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_JOB_MEMORY;
    limits.BasicLimitInformation.ActiveProcessLimit=16;
    limits.ProcessMemoryLimit=(SIZE_T)1024*1024*1024;
    limits.JobMemoryLimit=(SIZE_T)2*1024*1024*1024;
    if(!SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits))) goto done;
    cpu_limits.ControlFlags=JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP;
    cpu_limits.CpuRate=2500;
    if(!SetInformationJobObject(job,JobObjectCpuRateControlInformation,&cpu_limits,sizeof(cpu_limits))) goto done;
    printf("ResourcePolicy process=1073741824 job=2147483648 processes=16 cpuRate=2500\n");
    if(swprintf(command,8192,L"\"%ls\" \"%ls\" \"%ls\"",argv[3],argv[4],argv[5])<0) goto done;
    if(argc>=8) {
        size_t count;
        f=_wfopen(argv[7],L"rb"); if(!f) goto done;
        count=fread(command,sizeof(wchar_t),8191,f); fclose(f); command[count]=0;
    }
    environment=GetEnvironmentStringsW();
    if(!CreateProcessW(argv[3],command,NULL,NULL,TRUE,EXTENDED_STARTUPINFO_PRESENT|CREATE_SUSPENDED|CREATE_NO_WINDOW|CREATE_UNICODE_ENVIRONMENT,environment,argv[2],&startup.StartupInfo,&process)) goto done;
    if(!AssignProcessToJobObject(job,process.hProcess)) goto done;
    {
        HANDLE token=NULL; DWORD needed=0; TOKEN_APPCONTAINER_INFORMATION *info=NULL;
        LPWSTR sid=NULL, folder=NULL; wchar_t redirected[8192]; wchar_t *name,*end;
        if(!OpenProcessToken(process.hProcess,TOKEN_QUERY,&token)) goto done;
        GetTokenInformation(token,TokenAppContainerSid,NULL,0,&needed);
        info=(TOKEN_APPCONTAINER_INFORMATION*)malloc(needed);
        if(info && GetTokenInformation(token,TokenAppContainerSid,info,needed,&needed) && ConvertSidToStringSidW(info->TokenAppContainer,&sid) && SUCCEEDED(GetAppContainerFolderPath(sid,&folder))) {
            end=wcsrchr(folder,L'\\'); if(end) *end=0;
            name=wcsrchr(folder,L'\\');
            if(name && swprintf(redirected,8192,L"%ls\\Packages\\%ls\\AC\\Temp",argv[2],name+1)>0) {
                int mkdir_result=SHCreateDirectoryExW(NULL,redirected,NULL);
                printf("PrepareTemp=%d\n",mkdir_result);
            }
        }
        if(folder) CoTaskMemFree(folder);
        if(sid) LocalFree(sid);
        free(info); CloseHandle(token);
    }
    if(ResumeThread(process.hThread)==(DWORD)-1) goto done;
    if(argc==9) {
        for(;;) {
            DWORD wait=WaitForSingleObject(process.hProcess,50);
            if(wait==WAIT_OBJECT_0) break;
            if(wait!=WAIT_TIMEOUT) goto done;
            if(GetFileAttributesW(argv[8])!=INVALID_FILE_ATTRIBUTES) {
                if(!TerminateJobObject(job,1)) goto done;
                if(WaitForSingleObject(process.hProcess,15000)!=WAIT_OBJECT_0) goto done;
                printf("UserStop=1\n"); result=0; goto done;
            }
        }
    } else if(WaitForSingleObject(process.hProcess,180000)!=WAIT_OBJECT_0) goto done;
    if(!GetExitCodeProcess(process.hProcess,&exit_code)) goto done;
    printf("ChildExit=0x%08lX\n",(unsigned long)exit_code);
    result=exit_code==0 ? 0 : 72;
done:
    if(result!=0) printf("LauncherLastError=%lu\n",GetLastError());
    if(process.hProcess) TerminateProcess(process.hProcess,1);
    if(job) CloseHandle(job);
    if(process.hProcess) CloseHandle(process.hProcess);
    if(process.hThread) CloseHandle(process.hThread);
    if(out && out!=INVALID_HANDLE_VALUE) CloseHandle(out);
    if(input && input!=INVALID_HANDLE_VALUE) CloseHandle(input);
    if(startup.lpAttributeList) { DeleteProcThreadAttributeList(startup.lpAttributeList); free(startup.lpAttributeList); }
    if(environment) FreeEnvironmentStringsW(environment);
    close_env(env); FreeLibrary(module); return result;
}
