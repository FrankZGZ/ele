class eTactile{
  
  ElectrodeSwitch electrodeSwitch;
  TimerHandler timerHandler;
  
  private final int PC_ESP32_MEASURE_REQUEST             =0xFF; //Request to measure the impedance of all electrodes        
  private final int PC_ESP32_STIM_PATTERN                =0xFE; //Stimulation pattern for all electrodes
  private final int PC_ESP32_STIMULATION_POLARITY        =0xFD; //Polarity of the stimulation - ANODIC or CATHODIC
  private final int PC_ESP32_ELECTRODE_NUM               =0xFC; //Number of electrodes used for the stimulation
  private final int PC_ESP32_STIMULATION_PULSE_HEIGHT    =0xFB; //Pulse height of the stimulation
  private final int PC_ESP32_STIMULATION_PULSE_WIDTH     =0xFA; //Pulse width of the stimulation
  private final int PC_ESP32_SENSE_PULSE_HEIGHT          =0xF9; //Pulse height for impedance measurement
  private final int PC_ESP32_SENSE_PULSE_WIDTH           =0xF8; //Pulse width for impedance measurement
  private final int PC_ESP32_CHANNEL_DISCHARGE_TIME      =0xF7; //Discharge time for the channel
  private final int PC_ESP32_STIMULATION_FREQUENCY       =0xF6; //Frequency of the stimulation
  private final int PC_ESP32_HV513_NUM_REQUEST           =0xF5; //Request to get the number of HV513 modules used
  private final int ESP32_PC_RECEIVE_FINISHED            =0xAA; //Indicates that the ESP32 has received the data from the PC
  
  int     number_of_electrodes;
  int     stimulation_pulse_height;
  int     stimulation_pulse_width;
  int     sense_pulse_height;
  int     sense_pulse_width;
  int     channel_discharge_time;
  int     stimulation_frequency;
  int     hv513_num;
  int     delay_time;
  byte[]  electrode_mapping;
  byte[]  switch_state;
  int[]   voltages;
  
  int     polarity = 1;
  int     local_timer = 0;
  int     volume = 0, amplitude = 0, width_ = 50;
  int     executing_time = 0;          // Store the running sum of time of executed pattern
  boolean deactivate_flag = false; // For deactivating all the electrodes if there is an OFF time
  
  
  PApplet parent;  // Reference to the PApplet instance

  eTactile(PApplet parent) {
    this.parent = parent;
  }
  
  public void SETUP(String json_array, String json_pattern, String COM_PORT){
    dataLoader       = new LoadData();
    keyHandler       = new KeyPressHandler();
    electrodeSwitch  = new ElectrodeSwitch(switch_state, voltages);
    timerHandler     = new TimerHandler(executing_time, local_timer, deactivate_flag);
  
    dataLoader.load(json_array, json_pattern);
    
    
    //Initialize Serial Communication if the execution is true in JSON pattern data
    if (DEBUG != 0 && dataLoader.execution) {
      mySerialComm   = new SerialComm(parent, COM_PORT);
    }
    
    //Load Electrodes number and the Mapping function
    number_of_electrodes   = dataLoader.electrodes_number;
    electrode_mapping      = dataLoader.mapping_func;
    
    // Print electrode mapping
    System.out.println("Electrode Mapping:");
    for (int ch = 0; ch < electrode_mapping.length; ch++) {
        System.out.println("Channel " + ch + " -> Electrode " + electrode_mapping[ch]);
    }
    
    
    switch_state   = new byte[number_of_electrodes];
    voltages       = new int[number_of_electrodes];
  
    frameRate(60);
    
    graphicsManager.displayInstructions();
    electrodeSwitch.resetElectrodes(switch_state);
    
    /*
    //Setting the parameters for the stimulation
    if (DEBUG != 0 && dataLoader.execution) {
      send_electrode_number(number_of_electrodes);
      send_stimulation_pulse_height(20);
      send_stimulation_pulse_width(50);
      send_sense_pulse_height(5);
      send_sense_pulse_width(50);
      send_channel_discharge_time(50);
      send_stimulation_frequency(75);
    }
    */
  }
  
  public void stimulate(){
    JSONArray   pattern_array   = dataLoader.patterns.getJSONArray(local_timer); // Take the relevant pattern
    JSONObject  pattern_object  = pattern_array.getJSONObject(0);
    JSONArray   pattern_data    = pattern_object.getJSONArray("pattern");
    JSONObject  delay_object    = pattern_object.getJSONObject("delay"); // ON time and OFF time
    int         delay_on        = delay_object.getInt("ON");
    int         delay_off       = delay_object.getInt("OFF");
  
    electrodeSwitch.updateSwitchState(switch_state, pattern_data, delay_on, delay_off, deactivate_flag);
  
    delay_time  = electrodeSwitch.getDelayTime();
    amplitude   = volume;
  
    electrodeSwitch = new ElectrodeSwitch(switch_state, voltages);
  
    // Check if execution is set to true before communicating with microcontroller
    if (dataLoader.execution) {
      // If execution = true, communicate with microcontroller.
      send_stim_pattern(switch_state);
      
      //Measuring
      if (local_timer == 0) {
      get_voltage_readings();
      }
      
      //Condition :- execution: true, simulation: true
      if (dataLoader.simulation) {
        graphicsManager.visualizeAndExecute();
        graphicsManager.visualizeBoardShape();
        graphicsManager.showPulse();
        graphicsManager.drawElectrodes();
        fill(255);
      } 
      
      //Condition :- execution: true, simulation: false
      else {
        textSize(40);
        graphicsManager.executeOnly();
        graphicsManager.showPulse();
        
      }
  
    }
    else {
      //Condition :- execution: false, simulation: true
      if (dataLoader.simulation) {
        graphicsManager.visualizeOnly();
        graphicsManager.visualizeBoardShape();
        graphicsManager.drawElectrodes();
        fill(255);
      }
      
      //Condition :- execution: false, simulation: false
      else {
        graphicsManager.noAny();
      }
    }
  
    //Delaytime
    timerHandler.handleTimer(delay_time, delay_off);
    
    executing_time    = timerHandler.getExecutingTime();
    local_timer       = timerHandler.getLocalTimer();
    deactivate_flag   = timerHandler.getDeactivateFlag();
    }
    
    public void updatePulse(){
      volume   = keyHandler.getVolume();
      width_   = keyHandler.getWidth();
      polarity = keyHandler.getPolarity();
    }
    
 
 //______________________________________________________________________//
  
  boolean send_stimulation_polarity(int polarity){// 1 - Anodic, 0 - Cathodic
    mySerialComm.sendByte((byte)PC_ESP32_STIMULATION_POLARITY);
    mySerialComm.sendByte((byte)polarity);
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  boolean send_stimulation_pulse_height(int pulse_height){
    mySerialComm.sendByte((byte)PC_ESP32_STIMULATION_PULSE_HEIGHT);
    mySerialComm.sendByte((byte)pulse_height);
    stimulation_pulse_height = pulse_height;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  boolean send_stimulation_pulse_width(int pulse_width){
    mySerialComm.sendByte((byte)PC_ESP32_STIMULATION_PULSE_WIDTH);
    mySerialComm.sendByte((byte)pulse_width);
    stimulation_pulse_width = pulse_width;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  boolean send_sense_pulse_height(int pulse_height){
    mySerialComm.sendByte((byte)PC_ESP32_SENSE_PULSE_HEIGHT);
    mySerialComm.sendByte((byte)pulse_height);
    sense_pulse_height = pulse_height;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  boolean send_sense_pulse_width(int pulse_width){
    mySerialComm.sendByte((byte)PC_ESP32_SENSE_PULSE_WIDTH);
    mySerialComm.sendByte((byte)pulse_width);
    sense_pulse_width = pulse_width;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  boolean send_channel_discharge_time(int discharge_time){
    mySerialComm.sendByte((byte)PC_ESP32_CHANNEL_DISCHARGE_TIME);
    mySerialComm.sendByte((byte)discharge_time);
    channel_discharge_time = discharge_time;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  boolean send_stimulation_frequency(int stim_freq){
    mySerialComm.sendByte((byte)PC_ESP32_STIMULATION_FREQUENCY);
    mySerialComm.sendByte((byte)stim_freq);
    stimulation_frequency = stim_freq;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  boolean send_electrode_number(int electrode_number){
    mySerialComm.sendByte((byte)PC_ESP32_ELECTRODE_NUM);
    mySerialComm.sendByte((byte)electrode_number);
    number_of_electrodes = electrode_number;
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
 
  
  boolean send_stim_pattern(byte[] SwitchState) { // or int[] SwitchState
    if (SwitchState.length != number_of_electrodes){
      System.err.println("Stimulation pattern length should be equal to the number of electrodes");
    }
  
    if (mySerialComm.port != null && dataLoader.execution) {
      mySerialComm.sendByte((byte)PC_ESP32_STIM_PATTERN);
      byte[] StimPattern = new byte[number_of_electrodes];
      for (int ch = 0; ch < number_of_electrodes; ch++) {
        StimPattern[electrode_mapping[ch]] = SwitchState[ch];
      }
      mySerialComm.sendByteArray(StimPattern);
    } else {
      System.err.println("Serial port not initialized or execution not enabled.");
    }
    int rcv = mySerialComm.readByteWithTimeout(0.005);
    if (rcv == ESP32_PC_RECEIVE_FINISHED){
      return true;
    }
    else{
      return false;
    }
  }
  
  
  public void get_voltage_readings() {
    if (mySerialComm.port != null && dataLoader.execution) {
      mySerialComm.sendByte((byte)PC_ESP32_MEASURE_REQUEST);
        byte[] Record = new byte[number_of_electrodes];
        for (int ch = 0; ch < number_of_electrodes; ch++) {
          Record[ch] = (byte) mySerialComm.readByteWithTimeout(0.002); //(byte)port.read();
        }
        for (int ch = 0; ch < number_of_electrodes; ch++) {
          voltages[ch] = Record[electrode_mapping[ch]];
        }
    } else {
      System.err.println("Serial port not initialized or execution not enabled.");
    }
  }
  
  
  int update_and_get_hv513_count(){
    mySerialComm.sendByte((byte)PC_ESP32_HV513_NUM_REQUEST);
    int received_data = mySerialComm.readByteWithTimeout(0.005);
    hv513_num = received_data;
    return received_data;
  }
  
  
  
  boolean check_valid_initialization() {
    /* Check the safety of the parameters set for the application.
       The function will check whether the parameters set are safe or not. */

    if (number_of_electrodes == 0) {
        println("Number of electrodes is not set");
        return false;
    }
    if (stimulation_pulse_height == 0) {
        println("Stimulation pulse height is not set");
        return false;
    }
    if (stimulation_pulse_width == 0) {
        println("Stimulation pulse width is not set");
        return false;
    }
    if (sense_pulse_height == 0) {
        println("Sense pulse height is not set");
        return false;
    }
    if (sense_pulse_width == 0) {
        println("Sense pulse width is not set");
        return false;
    }
    if (channel_discharge_time == 0) {
        println("Channel discharge time is not set");
        return false;
    }
    if (stimulation_frequency == 0) {
        println("Stimulation frequency is not set");
        return false;
    }
    if (hv513_num == 0) {
        println("HV513 count is not set");
        return false;
    }
    if (hv513_num * 8 < number_of_electrodes) {
        println("Number of electrodes exceeds the number of outputs connected.\nPlease check the number of stacked switching modules");
        return false;
    }

    println("All parameters are set");
    return true;
  }
}
