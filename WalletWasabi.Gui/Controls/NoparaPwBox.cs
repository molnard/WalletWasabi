using Avalonia.Styling;
using System;
using System.Collections.Generic;
using System.Text;

namespace WalletWasabi.Gui.Controls
{
	public class NoparaPwBox : ExtendedTextBox, IStyleable
	{
		Type IStyleable.StyleKey => typeof(NoparaPwBox);

		static NoparaPwBox()
		{
			//FocusableProperty.OverrideDefaultValue(typeof(NoparaPwBox), true);
		}
		public NoparaPwBox()
		{

		}
	}
}
