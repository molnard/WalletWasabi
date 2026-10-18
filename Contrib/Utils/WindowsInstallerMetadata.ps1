[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Finalize')][string] $Mode,
    [Parameter(Mandatory = $true)][string] $Path,
    [Parameter(Mandatory = $true)][long] $SourceDateEpoch
)

$ErrorActionPreference = 'Stop'

if (-not ('Wasabi.Release.InstallerMetadata' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Wasabi.Release
{
	public static class InstallerMetadata
	{
		private const uint ReadStorage = 0x20; // STGM_READ | STGM_SHARE_DENY_WRITE.
		private const uint WriteStorage = 0x12; // STGM_READWRITE | STGM_SHARE_EXCLUSIVE.
		private const string EmptyPackageCode = "{00000000-0000-0000-0000-000000000000}";

		public static void Prepare(string path, long epoch)
		{
			// DOS timestamps have two-second precision. Round down before the
			// native conversion, which would otherwise round 2107's last second
			// into 2108 and cause WiX to fall back to the file creation date.
			DateTime timestamp = GetTimestamp(epoch).AddSeconds(-(epoch % 2));
			long local = timestamp.ToFileTimeUtc();
			long utc;
			// WiX uses FileTimeToLocalFileTime, whose current DST bias can differ
			// from DateTime's bias at the source date. Use its exact native inverse.
			if (!LocalFileTimeToFileTime(ref local, out utc))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			DateTime fileTime = DateTime.FromFileTimeUtc(utc);
			foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
			{
				File.SetLastWriteTimeUtc(file, fileTime);
			}
		}

		public static string Finalize(string path, long epoch)
		{
			long timestamp = GetTimestamp(epoch).ToFileTimeUtc();
			Check(MsiVerifyPackage(path), "Invalid Windows Installer package");
			WithStorage(path, ReadStorage, RejectSignature);

			// Hash the complete unsigned package with its own code replaced by a
			// sentinel. This includes installer logic, tools and payload changes.
			SetSummary(path, EmptyPackageCode, timestamp);
			WithStorage(path, WriteStorage, ClearStorageTimes);
			byte[] digest;
			using (SHA256 sha = SHA256.Create())
			using (FileStream file = File.OpenRead(path))
			{
				digest = sha.ComputeHash(file);
			}
			byte[] guid = new byte[16];
			Array.Copy(digest, guid, guid.Length);
			// RFC 9562 UUIDv8: SHA256-derived data with version and variant bits.
			guid[7] = (byte)((guid[7] & 0x0f) | 0x80);
			guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
			string packageCode = new Guid(guid).ToString("B").ToUpperInvariant();
			SetSummary(path, packageCode, timestamp);
			WithStorage(path, WriteStorage, ClearStorageTimes);
			return packageCode;
		}

		private static DateTime GetTimestamp(long epoch)
		{
			// Cabinet dates use DOS format: years 1980 through 2107.
			const long minimum = 315532800;
			const long maximum = 4354819199;
			if (epoch < minimum || epoch > maximum)
			{
				throw new ArgumentOutOfRangeException("epoch", "SOURCE_DATE_EPOCH must be within the cabinet date range (1980-2107).");
			}
			return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch);
		}

		private static void SetSummary(string path, string packageCode, long timestamp)
		{
			uint summary;
			Check(MsiGetSummaryInformation(0, path, 3, out summary), "Cannot open MSI summary information");
			try
			{
				Check(MsiSummaryInfoSetProperty(summary, 9, 30, 0, ref timestamp, packageCode), "Cannot set PackageCode");
				Check(MsiSummaryInfoSetProperty(summary, 12, 64, 0, ref timestamp, null), "Cannot set MSI creation time");
				Check(MsiSummaryInfoSetProperty(summary, 13, 64, 0, ref timestamp, null), "Cannot set MSI save time");
				Check(MsiSummaryInfoPersist(summary), "Cannot save MSI summary information");
			}
			finally
			{
				MsiCloseHandle(summary);
			}
		}

		private static void WithStorage(string path, uint mode, Action<IStorage> action)
		{
			Guid iid = typeof(IStorage).GUID;
			IStorage storage;
			StgOpenStorageEx(path, mode, 5, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out storage);
			try
			{
				action(storage);
			}
			finally
			{
				Marshal.FinalReleaseComObject(storage);
			}
		}

		private static List<STATSTG> GetElements(IStorage storage)
		{
			IEnumSTATSTG enumerator;
			storage.EnumElements(0, IntPtr.Zero, 0, out enumerator);
			try
			{
				List<STATSTG> elements = new List<STATSTG>();
				STATSTG[] entry = new STATSTG[1];
				int result;
				while ((result = enumerator.Next(1, entry, IntPtr.Zero)) == 0)
				{
					elements.Add(entry[0]);
				}
				Marshal.ThrowExceptionForHR(result);
				return elements;
			}
			finally
			{
				Marshal.FinalReleaseComObject(enumerator);
			}
		}

		private static void RejectSignature(IStorage storage)
		{
			foreach (STATSTG entry in GetElements(storage))
			{
				if (string.Equals(entry.pwcsName, "\u0005DigitalSignature", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(entry.pwcsName, "\u0005MsiDigitalSignatureEx", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("Refusing to modify a signed MSI. Finalize metadata before signing.");
				}
				if (entry.type == 1)
				{
					VisitStorage(storage, entry.pwcsName, ReadStorage, RejectSignature);
				}
			}
		}

		private static void ClearStorageTimes(IStorage storage)
		{
			ClearChildStorageTimes(storage);
			// The compound container also records the wall-clock build time.
			// Let Windows handle its FAT/DIFAT layout instead of patching bytes.
			long zero = 0;
			storage.SetElementTimes(null, ref zero, IntPtr.Zero, ref zero);
			storage.Commit(0);
		}

		private static void ClearChildStorageTimes(IStorage storage)
		{
			foreach (STATSTG entry in GetElements(storage))
			{
				if (entry.type == 1)
				{
					VisitStorage(storage, entry.pwcsName, WriteStorage, ClearChildStorageTimes);
					// Only the root accepts a null name. A substorage's times must
					// be set through its parent after the substorage is committed.
					long zero = 0;
					storage.SetElementTimes(entry.pwcsName, ref zero, IntPtr.Zero, ref zero);
				}
			}
			storage.Commit(0);
		}

		private static void VisitStorage(IStorage parent, string name, uint mode, Action<IStorage> action)
		{
			IStorage child;
			// Substorages require STGM_SHARE_EXCLUSIVE even for read access.
			uint childMode = (mode & 3) | 0x10;
			parent.OpenStorage(name, null, childMode, IntPtr.Zero, 0, out child);
			try
			{
				action(child);
			}
			finally
			{
				Marshal.FinalReleaseComObject(child);
			}
		}

		private static void Check(uint result, string message)
		{
			if (result != 0)
			{
				throw new Win32Exception((int)result, message);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool LocalFileTimeToFileTime(ref long local, out long utc);
		[DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiVerifyPackageW")]
		private static extern uint MsiVerifyPackage(string path);
		[DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
		private static extern uint MsiGetSummaryInformation(uint database, string path, uint count, out uint summary);
		[DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSummaryInfoSetPropertyW")]
		private static extern uint MsiSummaryInfoSetProperty(uint summary, uint property, uint type, int number, ref long fileTime, string text);
		[DllImport("msi.dll")]
		private static extern uint MsiSummaryInfoPersist(uint summary);
		[DllImport("msi.dll")]
		private static extern uint MsiCloseHandle(uint handle);
		[DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
		private static extern void StgOpenStorageEx(string path, uint mode, uint format, uint attributes, IntPtr options, IntPtr security, ref Guid iid, out IStorage storage);

		[ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IStorage
		{
			void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
			void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
			void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStorage storage);
			void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);
			void CopyTo(uint count, IntPtr excludeIids, IntPtr excludeNames, IStorage destination);
			void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);
			void Commit(uint flags);
			void Revert();
			void EnumElements(uint reserved1, IntPtr reserved2, uint reserved3, out IEnumSTATSTG enumerator);
			void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string name);
			void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string oldName, [MarshalAs(UnmanagedType.LPWStr)] string newName);
			void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string name, ref long created, IntPtr accessed, ref long modified);
			void SetClass(ref Guid clsid);
			void SetStateBits(uint bits, uint mask);
			void Stat(out STATSTG stat, uint flags);
		}

		[ComImport, Guid("0000000D-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IEnumSTATSTG
		{
			[PreserveSig]
			int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] STATSTG[] entries, IntPtr fetched);
			void Skip(uint count);
			void Reset();
			void Clone(out IEnumSTATSTG enumerator);
		}
	}
}
'@
}

$fullPath = [IO.Path]::GetFullPath($Path)
if ($Mode -eq 'Prepare') {
    [Wasabi.Release.InstallerMetadata]::Prepare($fullPath, $SourceDateEpoch)
} else {
    [Wasabi.Release.InstallerMetadata]::Finalize($fullPath, $SourceDateEpoch)
}
