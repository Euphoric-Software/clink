using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Windows.Win32
{
    internal static unsafe partial class ComHelpers
    {
        static partial void PopulateIUnknownImpl<TComInterface>(IUnknown.Vtbl* vtable) where TComInterface : unmanaged
        {
            ComWrappers.GetIUnknownImpl(out nint qi, out nint addRef, out nint release);
            vtable->QueryInterface_1 = (delegate* unmanaged[Stdcall]<IUnknown*, Guid*, void**, HRESULT>)qi;
            vtable->AddRef_2 = (delegate* unmanaged[Stdcall]<IUnknown*, uint>)addRef;
            vtable->Release_3 = (delegate* unmanaged[Stdcall]<IUnknown*, uint>)release;
        }
    }
}
