/* ETactileKit - Visualization and Execution programme */
import processing.serial.*;
import java.io.File;

// Software definitions
final private int DEBUG = 1;


LoadData dataLoader;
KeyPressHandler keyHandler;
GraphicsManager graphicsManager;
SerialComm mySerialComm;
eTactile eTactile;

void settings() {
  graphicsManager = new GraphicsManager();
  graphicsManager.setupGraphics();
}

void setup() {
  
  eTactile = new eTactile(this);
  
  String COM_PORT = "COM3";   //Change this according to your COM port
  
  // Add your simulation pattern JSON file name and array pattern JSON file name here
  String stimulation_pattern = "JSON_Files/example_main_board_4.json";
  String array_pattern = "JSON_Files/electrode_array_main_board.json";
  
  eTactile.SETUP(array_pattern,stimulation_pattern, COM_PORT);
}

void draw() {
 
  background(0);
  eTactile.stimulate();
} 

void keyPressed() {
  keyHandler.handleKeyPress(key);
  eTactile.updatePulse();
}
