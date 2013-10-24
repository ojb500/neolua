﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Neo.IronLua
{
  #region -- enum LuaRuntimeHelper ----------------------------------------------------

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary>Enumeration with the runtime-functions.</summary>
  internal enum LuaRuntimeHelper
  {
    /// <summary>Gets an object from a Result-Array.</summary>
    GetObject,
    /// <summary>Converts a value via the TypeConverter</summary>
    Convert,
    /// <summary>Creates the Result-Array for the return instruction</summary>
    ReturnResult,
    /// <summary>Concats the string.</summary>
    StringConcat,
    /// <summary>Sets the table from an initializion list.</summary>
    TableSetObjects,
    /// <summary>Concats Result-Array</summary>
    ConcatArrays
  } // enum LuaRuntimeHelper

  #endregion

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary>All static methods for the language implementation</summary>
  public partial class Lua
  {
    private static object luaStaticLock = new object();
    private static Dictionary<LuaRuntimeHelper, MethodInfo> runtimeHelperCache = new Dictionary<LuaRuntimeHelper, MethodInfo>();
    private static Dictionary<string, IDynamicMetaObjectProvider> luaSystemLibraries = new Dictionary<string, IDynamicMetaObjectProvider>(); // Array with system libraries
    private static Dictionary<string, Type> knownTypes = null; // Known types of the current AppDomain
    private static Dictionary<string, CoreFunction> luaFunctions = new Dictionary<string, CoreFunction>(); // Core functions for the object

    #region -- RtReturnResult, RtGetObject --------------------------------------------

    internal static object[] RtReturnResult(object[] objects)
    {
      // Gibt es ein Ergebnis
      if (objects == null || objects.Length == 0)
        return objects;
      else if (objects[objects.Length - 1] is object[]) // Ist das letzte Ergebnis ein Objekt-Array
      {
        object[] l = (object[])objects[objects.Length - 1];
        object[] n = new object[objects.Length - 1 + l.Length];

        // Kopiere die ersten Ergebnisse
        for (int i = 0; i < objects.Length - 1; i++)
          if (objects[i] is object[])
          {
            object[] t = (object[])objects[i];
            n[i] = t == null || t.Length == 0 ? null : t[0];
          }
          else
            n[i] = objects[i];

        // Füge die vom letzten Result an
        for (int i = 0; i < l.Length; i++)
          n[i + objects.Length - 1] = l[i];

        return n;
      }
      else
      {
        for (int i = 0; i < objects.Length; i++)
          if (objects[i] is object[])
          {
            object[] t = (object[])objects[i];
            objects[i] = t == null || t.Length == 0 ? null : t[0];
          }
        return objects;
      }
    } // func RtReturnResult

    internal static object RtGetObject(object[] values, int i)
    {
      if (values == null)
        return null;
      else if (i < values.Length)
        return values[i];
      else
        return null;
    } // func RtGetObject

    #endregion

    #region -- RtConcatArrays, RtStringConcat -----------------------------------------

    internal static Array RtConcatArrays(Type elementType, Array a, Array b, int iStartIndex)
    {
      int iCountB = b.Length - iStartIndex;

      Array r = Array.CreateInstance(elementType, a.Length + iCountB);
      if (a.Length > 0)
        Array.Copy(a, r, a.Length);
      if (iStartIndex < b.Length)
        Array.Copy(b, iStartIndex, r, a.Length, iCountB);

      return r;
    } // func RtConcatArrays

    internal static object RtStringConcat(string[] strings)
    {
      return String.Concat(strings);
    } // func RtStringConcat

    #endregion

    #region -- RtConvert --------------------------------------------------------------

    internal static object RtConvert(object value, Type to)
    {
      if (to == typeof(bool))
        return ConvertToBoolean(value);
      else if (value == null)
        if (to.IsValueType)
          return Activator.CreateInstance(to);
        else
          return null;
      else if (to.IsAssignableFrom(value.GetType()))
        return value;
      else
      {
        TypeConverter conv = TypeDescriptor.GetConverter(to);
        if (value == null)
          throw new ArgumentNullException(); // Todo: LuaException
        else if (conv.CanConvertFrom(value.GetType()))
          return conv.ConvertFrom(null, CultureInfo.InvariantCulture, value);
        else
        {
          conv = TypeDescriptor.GetConverter(value.GetType());
          if (conv.CanConvertTo(to))
            return conv.ConvertTo(null, CultureInfo.InvariantCulture, value, to);
          else
            throw new LuaRuntimeException(String.Format("'{0}' kann nicht in '{1}' konvertiert werden.", value, to.Name), null);
        }
      }
    } // func RtConvert

    private static bool ConvertToBoolean(object value)
    {
      if (value == null)
        return false;
      else if (value is bool)
        return (bool)value;
      else if (value is byte)
        return (byte)value != 0;
      else if (value is sbyte)
        return (sbyte)value != 0;
      else if (value is short)
        return (short)value != 0;
      else if (value is ushort)
        return (ushort)value != 0;
      else if (value is int)
        return (int)value != 0;
      else if (value is uint)
        return (uint)value != 0;
      else if (value is long)
        return (long)value != 0;
      else if (value is ulong)
        return (ulong)value != 0;
      else if (value is float)
        return (float)value != 0;
      else if (value is double)
        return (double)value != 0;
      else if (value is decimal)
        return (decimal)value != 0;
      else
        return true;
    } // func RtConvertToBoolean

    #endregion

    #region -- Table Objects ----------------------------------------------------------

    internal static object RtTableSetObjects(LuaTable t, object value, int iStartIndex)
    {
      if (value != null && (value is object[] || typeof(object[]).IsAssignableFrom(value.GetType())))
      {
        object[] v = (object[])value;

        for (int i = 0; i < v.Length; i++)
          t[iStartIndex++] = v[i];
      }
      else
        t[iStartIndex] = value;
      return t;
    } // func RtTableSetObjects

    #endregion

    #region -- GetRuntimeHelper -------------------------------------------------------

    internal static MethodInfo GetRuntimeHelper(LuaRuntimeHelper runtimeHelper)
    {
      MethodInfo mi;
      lock (luaStaticLock)
        if (!runtimeHelperCache.TryGetValue(runtimeHelper, out mi))
        {
          string sMemberName = "Rt" + runtimeHelper.ToString();

          mi = typeof(Lua).GetMethod(sMemberName, BindingFlags.NonPublic | BindingFlags.Static);
          if (mi == null)
            throw new ArgumentException(String.Format("RuntimeHelper {0} not resolved.", runtimeHelper));

          runtimeHelperCache[runtimeHelper] = mi;
        }
      return mi;
    } // func GetRuntimeHelper

    #endregion

    #region -- struct CoreFunction ----------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    internal struct CoreFunction
    {
      public Delegate GetDelegate(object self)
      {
        return Delegate.CreateDelegate(DelegateType, self, Method);
      } // func GetDelegate

      public MethodInfo Method;
      public Type DelegateType;
    } // struct CoreFunction

    #endregion

    #region -- TryGetSystemLibrary ----------------------------------------------------

    /// <summary>Gets the system library.</summary>
    /// <param name="library">Library</param>
    /// <returns>dynamic object for the library</returns>
    internal static bool TryGetSystemLibrary(string sLibraryName, out IDynamicMetaObjectProvider lib)
    {
      lock (luaStaticLock)
      {
        if (luaSystemLibraries.Count == 0)
        {
          foreach (Type t in typeof(Lua).GetNestedTypes(BindingFlags.NonPublic))
          {
            if (t.Name.StartsWith("LuaLibrary", StringComparison.OrdinalIgnoreCase))
            {
              string sName = t.Name.Substring(10).ToLower();
              luaSystemLibraries[sName] = new LuaPackageProxy(t);
            }
          }
        }
        return luaSystemLibraries.TryGetValue(sLibraryName, out lib);
      }
    } // func GetSystemLibrary

    #endregion

    #region -- GetType ----------------------------------------------------------------

    /// <summary>Resolve typename to a type.</summary>
    /// <param name="sTypeName">Fullname of the type</param>
    /// <returns>The resolved type or <c>null</c>.</returns>
    internal static Type GetType(string sTypeName)
    {
      Type type = Type.GetType(sTypeName, false);
      if (type == null)
        lock (luaStaticLock)
        {
          // Lookup the type in the cache
          if (knownTypes != null && knownTypes.TryGetValue(sTypeName, out type))
            return type;

          // Lookup the type in all loaded assemblies
          var asms = AppDomain.CurrentDomain.GetAssemblies();
          for (int i = 0; i < asms.Length; i++)
          {
            if ((type = asms[i].GetType(sTypeName, false)) != null)
              break;
          }

          // Put the type in the cache
          if (type != null)
          {
            if (knownTypes == null)
              knownTypes = new Dictionary<string, Type>();
            knownTypes[sTypeName] = type;
          }
        }
      return type;
    } // func GetType

    #endregion

    #region -- TryGetLuaFunction ------------------------------------------------------

    internal static bool TryGetLuaFunction(string sName, out CoreFunction function)
    {
      lock (luaStaticLock)
      {
        if (luaFunctions.Count == 0) // Collect all lua sys functions
        {
          foreach (var mi in typeof(LuaGlobal).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            if (mi.Name.StartsWith("lua", StringComparison.OrdinalIgnoreCase))
            {
              Type typeDelegate = Expression.GetDelegateType((from p in mi.GetParameters() select p.ParameterType).Concat(new Type[] { mi.ReturnType }).ToArray());
              luaFunctions[mi.Name.Substring(3).ToLower()] = new CoreFunction { Method = mi, DelegateType = typeDelegate };
            }
        }

        // Get the cached function
        if (luaFunctions.TryGetValue(sName, out function))
          return true;

        return false;
      }
    } // func TryGetLuaFunction 

    #endregion
  } // class Lua
}
