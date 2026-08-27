using System;
using System.Collections.Generic;

namespace Il2CppDumper
{
    public class StructInfo
    {
        public string TypeName;
        public bool IsValueType;
        public string Parent;
        public List<StructFieldInfo> Fields = new();
        public List<StructFieldInfo> StaticFields = new();
        public StructVTableMethodInfo[] VTableMethod = Array.Empty<StructVTableMethodInfo>();
        public List<StructRGCTXInfo> RGCTXs = new();
        public int TypeDefIndex = -1;
        public uint FieldsSize;
        public bool FieldsSizeComputed;
        public bool UseExplicitLayout;
    }

    public class StructFieldInfo
    {
        public string FieldTypeName;
        public string FieldName;
        public bool IsValueType;
        public bool IsCustomType;
        public bool IsEnum;
        public string EnumTypeName;
        public int Offset = -1;
        public uint LayoutSize;
    }

    public class EnumInfo
    {
        public string Name;
        public string UnderlyingTypeName;
        public uint UnderlyingSize;
        public List<EnumMemberInfo> Members = new();
    }

    public class EnumMemberInfo
    {
        public string Name;
        public string ValueLiteral;
        public long Value;
    }

    public class StructVTableMethodInfo
    {
        public string MethodName;
    }

    public class StructRGCTXInfo
    {
        public Il2CppRGCTXDataType Type;
        public string TypeName;
        public string ClassName;
        public string MethodName;
    }
}
