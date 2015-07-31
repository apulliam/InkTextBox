using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Globalization;
using Windows.System;
using Windows.UI.Input.Inking;
using Windows.UI.Text.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

// The Templated Control item template is documented at http://go.microsoft.com/fwlink/?LinkId=234235

namespace InkTextBox
{
    [TemplatePart(Name = TextBoxName, Type = typeof(TextBox))]
    [TemplatePart(Name = InkCanvasName, Type = typeof(InkCanvas))]
    [TemplatePart(Name = ContainerName, Type = typeof(StackPanel))]
    [TemplatePart(Name = InkBorderName, Type = typeof(Border))]
    [TemplatePart(Name = InkWindowName, Type = typeof(Popup))]
    public sealed class InkTextBox : Control
    {
        private const string TextBoxName = "textBox";
        private const string InkCanvasName = "inkCanvas";
        private const string ContainerName = "container";
        private const string InkBorderName = "inkBorder";
        private const string InkWindowName = "inkWindow";

        public static readonly DependencyProperty SelectionHighlightColorProperty =
            DependencyProperty.Register("SelectionHighlightColor", typeof(string), typeof(InkTextBox), new PropertyMetadata(null));

        public static readonly DependencyProperty InkCanvasPositionProperty =
            DependencyProperty.Register("InkCanvasPosition", typeof(InkCanvasPosition), typeof(InkTextBox), new PropertyMetadata(null));

        public static readonly DependencyProperty InkColorProperty =
            DependencyProperty.Register("InkColor", typeof(Windows.UI.Color), typeof(InkTextBox), new PropertyMetadata(null));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(InkTextBox), new PropertyMetadata(null));

        public static readonly DependencyProperty PenSizeProperty =
            DependencyProperty.Register("PenSize", typeof(double), typeof(InkTextBox), new PropertyMetadata(null));

        public static readonly DependencyProperty HeaderProperty =
          DependencyProperty.Register("Header", typeof(string), typeof(InkTextBox), new PropertyMetadata(null));

        private TextBox textBox;
        private InkCanvas inkCanvas;
        private Grid container;
        private Border inkBorder;
        private Popup inkWindow;

        InkRecognizerContainer inkRecognizerContainer = null;
        private IReadOnlyList<InkRecognizer> recognizers = null;
        private Language previousInputLanguage = null;
        private CoreTextServicesManager textServiceManager = null;
        private static InkTextBox ActiveTimerControl = null;
        private DispatcherTimer recognitionTimer = null;
        private DispatcherTimer pointerTimer = null;
        private int lastSelectionStart;
        private int lastSelectionLength;
        private string lastSelection;

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public string SelectionHighlightColor
        {
            get { return (string)GetValue(SelectionHighlightColorProperty); }
            set { SetValue(SelectionHighlightColorProperty, value); }
        }

        public InkCanvasPosition InkCanvasPosition
        {
            get { return (InkCanvasPosition)GetValue(InkCanvasPositionProperty); }
            set { SetValue(InkCanvasPositionProperty, value); }
        }

        public Windows.UI.Color InkColor
        {
            get { return (Windows.UI.Color)GetValue(InkColorProperty); }
            set { SetValue(InkColorProperty, value); }
        }

        public double PenSize
        {
            get { return (double)GetValue(PenSizeProperty); }
            set { SetValue(PenSizeProperty, value); }
        }

        public string Header
        {
            get { return (string)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public InkTextBox()
        {
            this.DefaultStyleKey = typeof(InkTextBox);
            this.IsTabStop = false;
        }

        protected override void OnApplyTemplate()
        {
            container = this.GetTemplateChild(ContainerName) as Grid;
            textBox = this.GetTemplateChild(TextBoxName) as TextBox;
            inkCanvas = this.GetTemplateChild(InkCanvasName) as InkCanvas;
            inkBorder = this.GetTemplateChild(InkBorderName) as Border;
            inkWindow = this.GetTemplateChild(InkWindowName) as Popup;

            inkRecognizerContainer = new InkRecognizerContainer();
            recognizers = inkRecognizerContainer.GetRecognizers();

            // Set the text services so we can query when language changes
            textServiceManager = CoreTextServicesManager.GetForCurrentView();
            textServiceManager.InputLanguageChanged += TextServiceManager_InputLanguageChanged;

            SetDefaultRecognizerByCurrentInputMethodLanguageTag();

            // Create a timer that expires after 1 second
            recognitionTimer = new DispatcherTimer();
            recognitionTimer.Interval = new TimeSpan(0, 0, 1);
            recognitionTimer.Tick += RecoTimer_Tick;

            pointerTimer = new DispatcherTimer();
            pointerTimer.Interval = new TimeSpan(0, 0, 2);
            pointerTimer.Tick += PointerTimer_Tick;

            textBox.PointerEntered += TextBox_PointerEntered;
            textBox.AddHandler(PointerPressedEvent, new PointerEventHandler(TextBox_PointerPressed), true);
            textBox.SelectionChanged += TextBox_SelectionChanged;

            inkCanvas.PointerEntered += InkCanvas_PointerEntered;
            inkCanvas.InkPresenter.StrokesCollected += InkCanvas_StrokesCollected;
            inkCanvas.InkPresenter.StrokeInput.StrokeStarted += InkCanvas_StrokeStarted;
            inkCanvas.InkPresenter.StrokesErased += InkPresenter_StrokesErased;
            
            // Initialize drawing attributes. These are used in inking mode.
            InkDrawingAttributes drawingAttributes = new InkDrawingAttributes();
            drawingAttributes.Color = InkColor;
            double penSize = PenSize;
            drawingAttributes.Size = new Windows.Foundation.Size(penSize, penSize);
            drawingAttributes.IgnorePressure = false;
            drawingAttributes.FitToCurve = true;

            // Initialize the InkCanvas
            inkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(drawingAttributes);
            inkCanvas.InkPresenter.InputDeviceTypes = Windows.UI.Core.CoreInputDeviceTypes.Pen;

            //InputPane inputPane = InputPane.GetForCurrentView();
            //inputPane.Showing += InputPane_Showing;

            this.SizeChanged += InkTextBox_SizeChanged;

            enableText();
            base.OnApplyTemplate();
        }

        //private void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args)
        //{
        //    System.Diagnostics.Debug.WriteLine("InputPane.Showing");
        //    args.EnsuredFocusedElementInView = false;
        //}

        private void enableText()
        {
            inkWindow.IsOpen = false;
            textBox.IsReadOnly = false;
            System.Diagnostics.Debug.WriteLine("Enabling TextBox");
            textBox.Visibility = Visibility.Visible;
        }

        private void enableInk()
        {
            if (ActiveTimerControl != null)
            {
                ActiveTimerControl.enableText();
            }
            ActiveTimerControl = this;
            pointerTimer.Start();
           
            if (InkCanvasPosition == InkCanvasPosition.OverTextAndRight)
                textBox.Visibility = Visibility.Collapsed;
            else
            {
                textBox.IsReadOnly = true;
            }
            inkWindow.IsOpen = true;
            
        }


        private void InkTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var currentWindow = Window.Current;
            var ttv = container.TransformToVisual(currentWindow.Content);
            Point containerTopLeft = ttv.TransformPoint(new Point(0, 0));
            Point containerTopRight = ttv.TransformPoint(new Point(container.ActualWidth, 0));
            var adjustedAppX = currentWindow.Bounds.Right - currentWindow.Bounds.Left;
            var rightMargin = adjustedAppX - containerTopRight.X;
            var targetWidth = container.ActualWidth + rightMargin;

            System.Diagnostics.Debug.WriteLine("Adjusted app x = {0}", adjustedAppX);
            System.Diagnostics.Debug.WriteLine("App left = {0}", currentWindow.Bounds.Left);
            System.Diagnostics.Debug.WriteLine("Container left = {0}", containerTopLeft.X);
            System.Diagnostics.Debug.WriteLine("App right = {0}", currentWindow.Bounds.Right);
            System.Diagnostics.Debug.WriteLine("Container right = {0}", containerTopRight.X);
            System.Diagnostics.Debug.WriteLine("App width = {0}", currentWindow.Bounds.Width);
            System.Diagnostics.Debug.WriteLine("Container width = {0}", container.ActualWidth);
            System.Diagnostics.Debug.WriteLine("Right margin = {0}", rightMargin);
            System.Diagnostics.Debug.WriteLine("Target width = {0}", targetWidth);
            System.Diagnostics.Debug.WriteLine("<================================================================>");

            if (InkCanvasPosition == InkCanvasPosition.BelowTextFullWindow)
            {
                inkBorder.Width = currentWindow.Bounds.Width - 40;
                inkWindow.HorizontalOffset = -containerTopLeft.X + 20;
                inkWindow.VerticalOffset = textBox.ActualHeight;
            }
            else if (InkCanvasPosition == InkCanvasPosition.BelowTextAndRight)
            {
                inkBorder.Width = targetWidth - 20;
                inkWindow.HorizontalOffset = 0;
                inkWindow.VerticalOffset = textBox.ActualHeight;
            }
            else if (InkCanvasPosition == InkCanvasPosition.OverTextAndRight)
            {
                inkBorder.Width = targetWidth - 20;
                inkWindow.HorizontalOffset = 0;
            }

            inkBorder.Height = 150;
        }


        private void TextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox.SelectionStart != lastSelectionStart || textBox.SelectionLength != lastSelectionLength)
            {
              
                inkCanvas.InkPresenter.StrokeContainer.Clear();
                lastSelection = textBox.SelectedText;
                lastSelectionStart = textBox.SelectionStart;
                lastSelectionLength = textBox.SelectionLength;
            }
        }

        private void TextBox_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("TextBox.PointerEntered");
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Pen)
            {
                if (inkWindow.IsOpen)
                {
                    e.Handled = true;
                    System.Diagnostics.Debug.WriteLine("Handle PointerEntered for pen");

                }
                else
                {
                    e.Handled = true;
                    enableInk();
                    
                }
            }
        }

        private void TextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("TextBox.PointerPressed");
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Pen)
            {
                //if (inkWindow.IsOpen)
                {
                    e.Handled = true;
                    System.Diagnostics.Debug.WriteLine("Handle PointerEntered for pen");

                }
           
            }
        }

        private void InkPresenter_StrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
        {
            pointerTimer.Stop();
            recognitionTimer.Start();
        }

        /// <summary>
        /// Start countdown timer once the user has finished inking
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void InkCanvas_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            pointerTimer.Stop();
            recognitionTimer.Start();
        }

        /// <summary>
        /// Stops the timer if a new stroke has started
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void InkCanvas_StrokeStarted(InkStrokeInput sender, Windows.UI.Core.PointerEventArgs args)
        {
            recognitionTimer.Stop();
        }
        /// <summary>
        /// Once timer expires, recognizes the ink stroke as text.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RecoTimer_Tick(object sender, object e)
        {
            recognitionTimer.Stop();
            OnRecognizeAsync(null, null);
        }

        private void PointerTimer_Tick(object sender, object e)
        {
            pointerTimer.Stop();
            enableText();
            textBox.Focus(FocusState.Programmatic);
        }

        private void InkCanvas_PointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Touch)
            {
                enableText();
            }
        }

        async void OnRecognizeAsync(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            IReadOnlyList<InkStroke> currentStrokes = inkCanvas.InkPresenter.StrokeContainer.GetStrokes();
            if (currentStrokes.Count > 0)
            {
                try
                {
                    var recognitionResults = await inkRecognizerContainer.RecognizeAsync(inkCanvas.InkPresenter.StrokeContainer, InkRecognitionTarget.All);

                    if (recognitionResults.Count > 0)
                    {
                        // Display recognition result
                        String str = "";
                        for (int i = 0; i < recognitionResults.Count; i++)
                        {

                            str += recognitionResults[i].GetTextCandidates()[0];
                            if (i < recognitionResults.Count - 1)
                                str += " ";
                        }

                        var existingText = textBox.Text;
                        string newText;

                        if (textBox.SelectionLength != 0)
                        {
                            newText = existingText.Replace(textBox.SelectedText, str);

                        }
                        else
                            newText = existingText.Insert(lastSelectionStart, str);
                        textBox.Text = newText;
                        lastSelectionLength = str.Length;
                        textBox.Select(lastSelectionStart, str.Length);
                    }
                }
                catch (Exception ex)
                {
                    //rootPage.NotifyUser("Recognize operation failed: " + ex.Message, NotifyType.ErrorMessage);
                }
            }

            enableText();
            textBox.Focus(FocusState.Programmatic);
            var focusState = textBox.FocusState;
        }


        private void TextServiceManager_InputLanguageChanged(CoreTextServicesManager sender, object args)
        {
            SetDefaultRecognizerByCurrentInputMethodLanguageTag();
        }

        private void SetDefaultRecognizerByCurrentInputMethodLanguageTag()
        {
            // Query recognizer name based on current input method language tag (bcp47 tag)
            Language currentInputLanguage = textServiceManager.InputLanguage;

            if (currentInputLanguage != previousInputLanguage)
            {
                // try query with the full BCP47 name
                string recognizerName = RecognizerHelper.LanguageTagToRecognizerName(currentInputLanguage.LanguageTag);

                if (recognizerName != string.Empty)
                {
                    for (int index = 0; index < recognizers.Count; index++)
                    {
                        if (recognizers[index].Name == recognizerName)
                        {
                            inkRecognizerContainer.SetDefaultRecognizer(recognizers[index]);
                            previousInputLanguage = currentInputLanguage;
                            break;
                        }
                    }
                }
            }
        }
    }
}
