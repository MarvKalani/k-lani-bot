#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KLaniBot.Platforms.Windows;

// Programming discovery reads the installed COM type library, not screen positions or an assumed documentation version.
// Member metadata is returned in bounded pages and never invokes the described methods.
internal static class WindowsOfficeApiCatalog
{
    // The first two IDispatch slots provide type metadata without invoking an application operation.
    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DispatchTypeInformation
    {
        void GetTypeInfoCount(out uint count);
        void GetTypeInfo(uint index, int locale, out ITypeInfo information);
    }

    public static ApplicationApiPage Read(object application, string typeName, int offset, int limit)
    {
        var dispatch = (DispatchTypeInformation)application;
        dispatch.GetTypeInfo(0, 0, out ITypeInfo information);
        information.GetContainingTypeLib(out ITypeLib library, out _);
        int total = library.GetTypeInfoCount();
        if (typeName.Length == 0)
        {
            return ListTypes(library, total, offset, limit);
        }
        return ReadType(library, total, typeName, offset, limit);
    }

    private static ApplicationApiPage ListTypes(ITypeLib library, int total, int offset, int limit)
    {
        var names = new List<string>();
        for (int index = offset; index < Math.Min(total, offset + limit); index++)
        {
            library.GetDocumentation(index, out string name, out _, out _, out _);
            names.Add(name);
        }

        return new("", total, NextOffset(total, offset, limit), names.ToArray(), []);
    }

    // Throws ArgumentException when the selected object type is not in this instance's type library.
    private static ApplicationApiPage ReadType(ITypeLib library, int total, string name, int offset, int limit)
    {
        for (int index = 0; index < total; index++)
        {
            library.GetDocumentation(index, out string current, out _, out _, out _);
            if (!string.Equals(current.TrimStart('_'), name.TrimStart('_'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            library.GetTypeInfo(index, out ITypeInfo type);
            type.GetTypeAttr(out IntPtr attributes);
            try
            {
                TYPEATTR description = Marshal.PtrToStructure<TYPEATTR>(attributes);
                if (description.cFuncs > 0)
                {
                    return ReadMembers(type, current, description.cFuncs, offset, limit);
                }
            }
            finally
            {
                type.ReleaseTypeAttr(attributes);
            }
        }
        throw new ArgumentException("The selected type has no callable members in the installed object library.");
    }

    private static ApplicationApiPage ReadMembers(ITypeInfo type, string name, int total, int offset, int limit)
    {
        var members = new List<ApiMember>();
        for (int index = offset; index < Math.Min(total, offset + limit); index++)
        {
            members.Add(ReadMember(type, index));
        }

        return new(name, total, NextOffset(total, offset, limit), [], members.ToArray());
    }

    private static ApiMember ReadMember(ITypeInfo type, int index)
    {
        type.GetFuncDesc(index, out IntPtr pointer);
        try
        {
            FUNCDESC function = Marshal.PtrToStructure<FUNCDESC>(pointer);
            type.GetDocumentation(function.memid, out string name, out _, out _, out _);
            ApiMemberKind kind = function.invkind == INVOKEKIND.INVOKE_FUNC ? ApiMemberKind.Method
                : function.invkind == INVOKEKIND.INVOKE_PROPERTYGET ? ApiMemberKind.Get : ApiMemberKind.Set;
            string result = ((VarEnum)function.elemdescFunc.tdesc.vt).ToString();

            return new(name, kind, function.cParams, result, ReadParameters(type, function));
        }
        finally
        {
            type.ReleaseFuncDesc(pointer);
        }
    }

    private static ApiParameter[] ReadParameters(ITypeInfo type, FUNCDESC function)
    {
        var names = new string[function.cParams + 1];
        type.GetNames(function.memid, names, names.Length, out int named);
        var parameters = new List<ApiParameter>();
        int size = Marshal.SizeOf<ELEMDESC>();
        for (int index = 0; index < function.cParams; index++)
        {
            ELEMDESC element = Marshal.PtrToStructure<ELEMDESC>(function.lprgelemdescParam + index * size);
            PARAMFLAG flags = element.desc.paramdesc.wParamFlags;
            string name = index + 1 < named ? names[index + 1] : "parameter" + (index + 1);
            parameters.Add(new(name, ((VarEnum)element.tdesc.vt).ToString(),
                flags.HasFlag(PARAMFLAG.PARAMFLAG_FIN), flags.HasFlag(PARAMFLAG.PARAMFLAG_FOUT), flags.HasFlag(PARAMFLAG.PARAMFLAG_FOPT)));
        }

        return parameters.ToArray();
    }

    private static int? NextOffset(int total, int offset, int limit)
    {
        return offset + limit < total ? offset + limit : null;
    }
}
#endif
