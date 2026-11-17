
import controlP5.*;
import processing.data.*;

JSONObject json;           //json file write
JSONArray outerArray;
JSONArray outer_inner_Array;
JSONObject innerObject;
JSONObject most_innerObject;
JSONArray DataArray;

JSONArray Electrode_Array; // Create json Array for storing Electrode Array details
int Electode_Size = 80; // Adjust size of electrodes
PVector previousPoint;
PVector marked_electrodePos;
ArrayList<PVector> drawnPath; // Track the drawn path
ArrayList<PVector> previouslyMarked; // Track previously marked electrodes

int[] Elecs_Data;       
int Electrodes_number;

int visualizationValue;
int executionValue;

boolean flag = false;

boolean savePattern = false;

ControlP5 cp5; // ControlP5 object
Button saveButton;
Button finishButton; // Declare a button for finishing
Textfield ONtimefield;
Textfield OFFtimefield;
Button addButton;

int ON_time;
int OFF_time;

String ON_timeText = " ";
String OFF_timeText = " ";

LoadData dataLoader;
Visualizer visualizer;
MarkElectrodes markelectrodes;
MouseControls mouse;
SavePattern save;
Control control;

void setup() {
  
  dataLoader = new LoadData();
  
  json = new JSONObject();
  dataLoader.load();
  
  Electrode_Array = dataLoader.Electrode_Array;
  Electrodes_number = dataLoader.Electrodes_number;
  
  visualizer = new Visualizer(Electrode_Array, Electrodes_number);
  markelectrodes = new MarkElectrodes();
  mouse = new MouseControls();
  save = new SavePattern();
  control = new Control();
  
  outerArray = new JSONArray(); //json file write
  
  
  size(1000, 900); // Adjust window size according to your preference
  background(255); // White background
  visualizer.visualizeArrayPattern();
  previousPoint = new PVector(mouseX, mouseY);
  drawnPath = new ArrayList<PVector>();
  previouslyMarked = new ArrayList<PVector>();
  
  //This should inside the draw()
  Elecs_Data = new int[Electrodes_number];
  
  // Create UI button
  cp5 = new ControlP5(this);
  saveButton = cp5.addButton("toggleSave")
                   .setPosition(10, 10)
                   .setSize(100, 30)
                   .setCaptionLabel("Save Pattern");
                   
  finishButton = cp5.addButton("finish")
                    .setPosition(120, 10)
                    .setSize(100, 30)
                    .setCaptionLabel("Finish")
                    .setVisible(false); // Initially hide the button
                    
  //_________________________________________________________________________   
    // Add a text label for value1
  cp5.addTextlabel("ON_time")
     .setText("ON time:")
     .setPosition(620, 50)
     .setColor(color(0))
     .setFont(createFont("Arial", 18)); // Set font size
  
  // Create a textfield for value1
  ONtimefield = cp5.addTextfield("ONtimefield")
                  .setPosition(750, 50)
                  .setSize(200, 30)
                  .setAutoClear(true);
                  
  // Add a text label for value2
  cp5.addTextlabel("OFF_time")
     .setText("OFF time:")
     .setPosition(620, 100)
     .setColor(color(0))
     .setFont(createFont("Arial", 18)); // Set font size
  
  // Create a textfield for value2
  OFFtimefield = cp5.addTextfield("OFFtimefield")
                  .setPosition(750, 100)
                  .setSize(200, 30)
                  .setAutoClear(true);
                  
  // Create a button
  cp5.addButton("submit")
     .setLabel("Submit")
     .setPosition(750, 150)
     .setSize(100, 40);
     
     
   ON_timeText = "ON time is: " + ON_time;
   OFF_timeText = "OFF time is: " + OFF_time;
   
   keyPressed();
 //__________________________________________________________________________________ 
 
 // Create drop-down button for Visualization
  DropdownList dlist = cp5.addDropdownList("Visualization")
                        .setPosition(750, 500)
                        .setSize(100, 100)
                        .setBarHeight(20)
                        .setItemHeight(20);
  
                        
  // Add options to the drop-down list
  dlist.addItem("NO", 0);
  dlist.addItem("YES", 1);
 
 // Add label for the drop-down button
  cp5.addTextlabel("VisualizationLabel")
     .setText("Visualization:")
     .setPosition(630, 500)
     .setColor(color(0))
     .setFont(createFont("Arial", 16));
     
     
// Create drop-down button for Execution
DropdownList executionList = cp5.addDropdownList("Execution")
                        .setPosition(750, 400) // Adjust position as needed
                        .setSize(100, 100) // Adjust size as needed
                        .setBarHeight(20)
                        .setItemHeight(20);
  
// Add options to the drop-down list
executionList.addItem("NO", 0);
executionList.addItem("YES", 1);

// Add label for the drop-down button
cp5.addTextlabel("ExecutionLabel")
   .setText("Execution:")
   .setPosition(630, 400) // Adjust position as needed
   .setColor(color(0))
   .setFont(createFont("Arial", 16)); // Set font and size

} 


void draw() {
  
 if (savePattern) { // Check if saving pattern is enabled
    print("ON time: " + ON_time + ",  ");
    println("OFF time: " + OFF_time);
    
    // Read selected value from Visualization drop-down
    visualizationValue = (int) cp5.get(DropdownList.class, "Visualization").getValue();
    println("Visualization Value: " + visualizationValue);
    
    // Read selected value from Execution drop-down
    executionValue = (int) cp5.get(DropdownList.class, "Execution").getValue();
    println("Execution Value: " + executionValue);
   
     save.savepattern();
    // Once saving is complete, stop saving pattern
    savePattern = false;
    toggleSave();
  }
  
  textAlign(LEFT);
  textFont(createFont("Arial", 18));
  fill(0);
  text(ON_timeText, 720, 240);
  text(OFF_timeText, 720, 280);  // How can I change this fonts to "Arial", 18
  
}

void toggleSave() {
  savePattern = !savePattern; // Toggle savePattern flag
  
  if (savePattern) {
    saveButton.setCaptionLabel("Stop Saving"); // Change button label
    drawnPath.clear(); // Clear drawn path when starting to save pattern
    finishButton.setVisible(false);
   
 
  } else {
    outer_inner_Array.append(innerObject);
    outerArray.append(outer_inner_Array);
    json.setJSONArray("Pattern_Data", outerArray);
    saveButton.setCaptionLabel("Save Pattern"); // Change button label
    previouslyMarked.clear();
    Elecs_Data = new int[Electrodes_number];
    
    // Set ON time and OFF time to zero
    ON_time = 0;
    OFF_time = 0;
    
    // Update displayed text
    ON_timeText = "ON time is: " + ON_time;
    OFF_timeText = "OFF time is: " + OFF_time;
    
    
    
    // Show the "Finish" button
    finishButton.setVisible(true);
  }
}





void mousePressed() {
  mouse.pressed();
}

void mouseDragged() {
  mouse.dragged();
  
}

void mouseReleased() {
  mouse.released();
}



void finish() {
  saveJSONObject(json, "new_test_1111.json");
  // Stop the program and hide the visualization
  exit();
}


// Handle enter key press for the sketch
void keyPressed() {
  control.handle(key);
}


void controlEvent(ControlEvent event) {
  if (event.isController()) {
    
    // Clear existing text by drawing a background rectangle over it
      fill(255); // Set fill color to white
      noStroke(); // Disable stroke
      rect(720, 200, 300, 150); // Draw a rectangle to clear existing text
      
    if (event.getController().getName().equals("submit")) {
      
      ON_time = int(ONtimefield.getStringValue());
      OFF_time = int(OFFtimefield.getStringValue());
      
      // Clear existing text by drawing a background rectangle over it
      fill(255); // Set fill color to white
      noStroke(); // Disable stroke
      rect(720, 200, 300, 150); // Draw a rectangle to clear existing text
      
       //Update displayed text
      ON_timeText = "ON time is: " + ON_time;
      OFF_timeText = "OFF time is: " + OFF_time;
      
      
    }
  }
}
